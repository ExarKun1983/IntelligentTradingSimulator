using System.Globalization;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubCiApproval(bool ActivationApproved = false, bool ApiCredentialVerified = false,
    bool TrustedWorkflowVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool AllowsCi => ActivationApproved && ApiCredentialVerified && TrustedWorkflowVerified && NoAdditionalChargesVerified;
}

public sealed record GitHubCiRoute(long WorkflowId, string Ref, string WorkflowSha);

/// <summary>Dispatches the trusted workflow, never a workflow definition from the coding branch.</summary>
public sealed class GitHubCiAdapter(GitHubRepositoryApi api, IStateStore store, GitHubCiRoute route,
    TimeProvider? clock = null, GitHubCiApproval? approval = null) : IEffectAdapter
{
    public const string WorkflowPath = ".github/workflows/agent-managed-ci.yml";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private static string Correlation(EffectIntent intent) => "managed-ci-" + StateJson.Digest(intent.Id + ":" + intent.Digest).ToLowerInvariant();

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (approval?.AllowsCi != true) throw new InvalidOperationException("CI prerequisites are not verified.");
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        ValidateEligibility(state, issue, intent);
        if (issue.Ci!.DispatchBoundary is not null) throw new InvalidDataException("Prepared CI dispatch is lookup-only.");
        await VerifyRouteAsync(token);
        if (!await LiveEligibleAsync(issue, token)) throw new InvalidDataException("CI head or open mapping changed.");
        var boundary = new CiDispatchBoundary(route.WorkflowId, route.WorkflowSha, route.Ref, Correlation(intent), clock.GetUtcNow());
        await SaveBoundaryAsync(intent, boundary, token);
        var latest = (await store.LoadAsync(token)).State;
        ValidateEligibility(latest, ValidateIntent(latest, intent), intent);
        await VerifyRouteAsync(token);
        if (!await LiveEligibleAsync(issue, token)) throw new InvalidDataException("CI head changed before dispatch.");
        latest = (await store.LoadAsync(token)).State;
        ValidateEligibility(latest, ValidateIntent(latest, intent), intent);
        using var response = await api.WriteAsync(HttpMethod.Post, $"actions/workflows/{route.WorkflowId}/dispatches",
            new { @ref = route.Ref, inputs = new { sha = intent.Sha, correlation = boundary.Correlation } }, token);
        var runId = response.RootElement.GetProperty("workflow_run_id").GetInt64();
        if (runId <= 0 || response.RootElement.GetProperty("run_url").GetString() != RunUrl(runId))
            throw new InvalidDataException("CI dispatch acknowledgement identity mismatch.");
        await SaveBoundaryAsync(intent, boundary with { RunId = runId }, token);
        return new(DeliveryStatus.Succeeded, runId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        if (issue.Ci!.DispatchBoundary is not { RunId: { } runId } boundary) return new(DeliveryStatus.Unknown);
        await api.VerifyRepositoryAsync(token);
        using var run = await api.GetAsync($"actions/runs/{runId}", token);
        ValidateRun(run.RootElement, runId, boundary);
        return new(DeliveryStatus.Succeeded, runId.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<OrchestrationEvent?> ReadEventAsync(RepositoryState state, IssueState issue, CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.Ci is not { Green: null, TerminalStopReason: null,
                DispatchBoundary: { RunId: { } runId } boundary } ci ||
            ci.RunId != runId.ToString(CultureInfo.InvariantCulture) || issue.PullRequest is null ||
            issue.ActiveTaskId != ci.TaskId || !issue.Tasks.TryGetValue(ci.TaskId, out var task) ||
            task.Status != TaskStatus.Completed || !task.LaunchConfirmed || task.FinalSha != ci.Sha || task.Round != ci.Round ||
            !state.Outbox.TryGetValue(ci.IntentId, out var intent) || intent.Status != DeliveryStatus.Succeeded)
            return null;
        await api.VerifyRepositoryAsync(token);
        var eligibleHead = await LiveEligibleAsync(issue, token) && issue.HeadSha == ci.Sha;
        using var run = await api.GetAsync($"actions/runs/{runId}", token);
        var root = run.RootElement;
        ValidateRun(root, runId, boundary);
        if (root.GetProperty("status").GetString() != "completed") return null;
        if (!eligibleHead) return Stopped("Managed CI reached a terminal outcome after its live head or open mapping changed.");
        var conclusion = root.GetProperty("conclusion").GetString();
        if (conclusion is not ("success" or "failure")) return Stopped("Managed CI did not complete with build/test evidence.");
        var jobs = new List<JsonElement>();
        for (var page = 1; page <= 21; page++)
        {
            using var response = await api.GetAsync($"actions/runs/{runId}/jobs?filter=latest&per_page=100&page={page}", token);
            var items = response.RootElement.GetProperty("jobs").EnumerateArray().ToList();
            jobs.AddRange(items.Select(item => item.Clone()));
            if (items.Count > 100 || jobs.Count > OrchestrationReducer.MaximumIndexEntries)
                throw new InvalidDataException("CI job pagination capacity exceeded.");
            if (items.Count < 100) break;
            if (page == 21) throw new InvalidDataException("CI job pagination incomplete.");
        }
        if (jobs.Count != 1) return Stopped("Managed CI job evidence is missing or ambiguous.");
        var job = jobs[0];
        if (job.GetProperty("id").GetInt64() <= 0 || job.GetProperty("run_id").GetInt64() != runId || job.GetProperty("run_attempt").GetInt32() != 1 ||
            job.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
            job.GetProperty("name").GetString() != "Test " + ci.Sha ||
            job.GetProperty("status").GetString() != "completed")
            return Stopped("Managed CI job identity does not match the trusted dispatch.");
        var steps = job.GetProperty("steps").EnumerateArray().ToList();
        string? Step(string name)
        {
            var matches = steps.Where(step => step.GetProperty("name").GetString() == name).ToList();
            return matches.Count == 1 && matches[0].GetProperty("status").GetString() == "completed"
                ? matches[0].GetProperty("conclusion").GetString() : null;
        }
        if (Step("Verify tested SHA") != "success") return Stopped("Managed CI did not verify the tested SHA.");
        var restore = Step("Restore dependencies");
        var build = Step("Build solution");
        var test = Step("Run tests");
        var green = conclusion == "success" && job.GetProperty("conclusion").GetString() == "success" &&
            restore == "success" && build == "success" && test == "success";
        var failed = conclusion == "failure" && job.GetProperty("conclusion").GetString() == "failure" &&
            restore == "success" && (build == "failure" || build == "success" && test == "failure");
        if (!green && !failed) return Stopped("Managed CI infrastructure or incomplete test evidence requires human reconciliation.");
        var blockers = green ? new List<Finding>() : [new Finding($"ci:{runId}",
            build == "failure" ? "Managed CI build failed." : "Managed CI tests failed.",
            $"Trusted run {RunUrl(runId)}; task {ci.TaskId}; round {ci.Round}; tested SHA {ci.Sha}.")];
        return Event(new CiResult(ci.RunId!, ci.TaskId, ci.Round, issue.PullRequest.Value, ci.Sha, green, blockers));

        OrchestrationEvent Stopped(string reason) => Event(new CiStopped(ci.RunId!, ci.TaskId, ci.Round, ci.Sha, reason));
        OrchestrationEvent Event(EventData data) => new($"ci-observed:{api.RepositoryId}:{issue.Generation}:{ci.IntentId}",
            api.Repository, issue.Number, state.Generation, issue.Generation, new("github-actions", ActorKind.CiService),
            root.GetProperty("updated_at").GetDateTimeOffset(), data);
    }

    private IssueState ValidateIntent(RepositoryState state, EffectIntent intent)
    {
        if (state.Repository != api.Repository || intent.Kind != EffectKind.RunCi ||
            !state.Outbox.TryGetValue(intent.Id, out var saved) || saved.Digest != intent.Digest ||
            saved.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown) ||
            saved.IssueNumber != intent.IssueNumber || saved.Generation != intent.Generation || saved.TaskId != intent.TaskId ||
            saved.Sha != intent.Sha || saved.Round != intent.Round || saved.ExternalId != intent.ExternalId ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue) || issue.Generation != intent.Generation ||
            issue.Ci?.IntentId != intent.Id || issue.Ci.TaskId != intent.TaskId || issue.Ci.Round != intent.Round || issue.Ci.Sha != intent.Sha)
            throw new InvalidDataException("CI effect is not the canonical executing dispatch.");
        return issue;
    }

    private static void ValidateEligibility(RepositoryState state, IssueState issue, EffectIntent intent)
    {
        if (state.AdmittedIssue != issue.Number || issue.Phase != Phase.CI_PENDING || !issue.ActiveAuthorized ||
            issue.Paused || issue.ReactivationRequired || issue.ClosurePending || issue.PendingFeedbackEventIds.Count != 0 ||
            issue.HeadSha != intent.Sha || issue.ActiveTaskId != intent.TaskId || issue.Ci!.Green is not null ||
            !issue.Tasks.TryGetValue(intent.TaskId!, out var task) || task.Status != TaskStatus.Completed ||
            !task.LaunchConfirmed || task.Round != intent.Round || task.FinalSha != intent.Sha)
            throw new InvalidDataException("CI requires explicit completed task/final SHA with no feedback or hold.");
    }

    private async Task VerifyRouteAsync(CancellationToken token)
    {
        if (route.WorkflowId <= 0 || !route.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) ||
            route.Ref.Length <= "refs/tags/".Length ||
            route.WorkflowSha.Length != 40 || !route.WorkflowSha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid trusted CI route.");
        await api.VerifyRepositoryAsync(token);
        using var workflow = await api.GetAsync($"actions/workflows/{route.WorkflowId}", token);
        if (workflow.RootElement.GetProperty("id").GetInt64() != route.WorkflowId ||
            workflow.RootElement.GetProperty("path").GetString() != WorkflowPath ||
            workflow.RootElement.GetProperty("state").GetString() != "active")
            throw new InvalidDataException("Trusted managed CI workflow is unavailable.");
        using var commit = await api.GetAsync("commits/" + Uri.EscapeDataString(route.Ref), token);
        if (commit.RootElement.GetProperty("sha").GetString() != route.WorkflowSha)
            throw new InvalidDataException("Trusted CI definition ref moved.");
    }

    private async Task<bool> LiveEligibleAsync(IssueState issue, CancellationToken token)
    {
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        using var pull = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        if (liveIssue.RootElement.GetProperty("number").GetInt32() != issue.Number ||
            pull.RootElement.GetProperty("number").GetInt32() != issue.PullRequest ||
            pull.RootElement.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.RootElement.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("CI live repository/mapping identity mismatch.");
        return liveIssue.RootElement.GetProperty("state").GetString() == "open" &&
            pull.RootElement.GetProperty("state").GetString() == "open" &&
            pull.RootElement.GetProperty("head").GetProperty("sha").GetString() == issue.HeadSha;
    }

    private void ValidateRun(JsonElement run, long runId, CiDispatchBoundary boundary)
    {
        if (run.GetProperty("id").GetInt64() != runId || run.GetProperty("workflow_id").GetInt64() != boundary.WorkflowId ||
            run.GetProperty("path").GetString() != WorkflowPath || run.GetProperty("event").GetString() != "workflow_dispatch" ||
            run.GetProperty("head_sha").GetString() != boundary.WorkflowSha || run.GetProperty("run_attempt").GetInt32() != 1 ||
            run.GetProperty("display_title").GetString() != boundary.Correlation ||
            run.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("CI run identity/definition/correlation mismatch.");
    }

    private string RunUrl(long id) => $"https://api.github.com/repos/{api.Repository}/actions/runs/{id}";

    private async Task SaveBoundaryAsync(EffectIntent intent, CiDispatchBoundary boundary, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            var issue = ValidateIntent(snapshot.State, intent);
            if (boundary.RunId is null && issue.Ci!.DispatchBoundary is not null)
                throw new InvalidDataException("Another executor already prepared this CI dispatch.");
            var next = new OrchestrationReducer().RecordCiBoundary(snapshot.State, intent.Id, boundary);
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("CI receipt publication conflict; dispatch remains lookup-only.");
    }
}
