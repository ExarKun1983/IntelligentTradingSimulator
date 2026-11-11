using System.Globalization;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubRequiredStatusApproval(bool ActivationApproved = false, bool CredentialActorVerified = false,
    bool TrustedPublisherVerified = false, bool RequiredContextVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool AllowsPublication => ActivationApproved && CredentialActorVerified && TrustedPublisherVerified &&
        RequiredContextVerified && NoAdditionalChargesVerified;
}

public sealed record GitHubStatusPublisher(long ActorId, string Login, string NodeId, string Type);

/// <summary>REST statuses target the tested PR SHA, not the isolated workflow's definition SHA.
/// A durable prepared boundary makes all interrupted creates lookup-only. Receipt recovery
/// confirms delivery, never lifecycle eligibility; late writes cannot be atomically revoked.</summary>
public sealed class GitHubRequiredStatusAdapter(GitHubRepositoryApi api, IStateStore store,
    GitHubStatusPublisher publisher, GitHubRequiredStatusApproval? approval = null,
    TimeProvider? clock = null) : IEffectAdapter
{
    public const string RecoveryLimitation =
        "GitHub commit-status creation is not transactional with canonical state or human feedback. " +
        "A late uncertain success may overwrite a newer error. Preserve the hold and require human " +
        "reconciliation; status receipt recovery never approves work or guarantees atomic revocation. " +
        "Before explicit reactivation, inspect the exact repository/SHA/context, verify the publisher and " +
        "latest corrective status, and acknowledge late-write risk. A missing lookup result is not proof of non-creation.";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (approval?.AllowsPublication != true) throw new InvalidOperationException("Required-status prerequisites are not verified.");
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        if (intent.RequiredStatusBoundary is not null || state.Outbox[intent.Id].RequiredStatusBoundary is not null)
            throw new InvalidDataException("Prepared status publication is lookup-only.");
        await PreflightAsync(state, issue, intent, token);
        var statuses = await ReadStatusesAsync(intent, token);
        var boundary = new RequiredStatusBoundary(publisher.ActorId, publisher.Login, publisher.NodeId,
            publisher.Type, clock.GetUtcNow(), statuses.Select(item => item.GetProperty("id").GetInt64()).ToList());
        await SaveBoundaryAsync(intent, boundary, token);
        state = (await store.LoadAsync(token)).State;
        issue = ValidateIntent(state, intent);
        await PreflightAsync(state, issue, intent, token);
        state = (await store.LoadAsync(token)).State;
        ValidateCurrent(state, ValidateIntent(state, intent), intent);
        var payload = intent.RequiredStatus!;
        using var receipt = await api.WriteAsync(HttpMethod.Post, $"statuses/{intent.Sha}",
            new { state = payload.State, context = payload.Context, description = payload.Description, target_url = payload.TargetUrl }, token);
        var id = ValidateStatus(receipt.RootElement, intent, boundary);
        if (boundary.ExistingStatusIds.Contains(id)) throw new InvalidDataException("Status receipt predates the creation boundary.");
        await SaveBoundaryAsync(intent, boundary with { StatusId = id }, token);
        state = (await store.LoadAsync(token)).State;
        issue = ValidateIntent(state, intent);
        await PreflightAsync(state, issue, intent, token);
        var confirmed = (await ReadStatusesAsync(intent, token)).Where(item => item.GetProperty("id").GetInt64() == id).ToList();
        if (confirmed.Count != 1) throw new InvalidDataException("Created status is not visible on the exact tested SHA.");
        ValidateStatus(confirmed[0], intent, boundary);
        state = (await store.LoadAsync(token)).State;
        ValidateCurrent(state, ValidateIntent(state, intent), intent);
        return new(DeliveryStatus.Succeeded, id.ToString(CultureInfo.InvariantCulture));
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        ValidateIntent(state, intent);
        if (state.Outbox[intent.Id].RequiredStatusBoundary is not { } boundary) return new(DeliveryStatus.Unknown);
        ValidatePublisher(boundary);
        await api.VerifyRepositoryAsync(token);
        var statuses = await ReadStatusesAsync(intent, token);
        var candidates = statuses.Where(item => boundary.StatusId is { } known
            ? item.GetProperty("id").GetInt64() == known
            : !boundary.ExistingStatusIds.Contains(item.GetProperty("id").GetInt64()) &&
              item.GetProperty("context").GetString() == intent.RequiredStatus!.Context &&
              item.GetProperty("target_url").GetString() == intent.RequiredStatus.TargetUrl).ToList();
        if (candidates.Count != 1) return new(DeliveryStatus.Unknown);
        var id = ValidateStatus(candidates[0], intent, boundary);
        return new(DeliveryStatus.Succeeded, id.ToString(CultureInfo.InvariantCulture));
    }

    private IssueState ValidateIntent(RepositoryState state, EffectIntent intent)
    {
        if (state.Repository != api.Repository || intent.Kind != EffectKind.PublishRequiredStatus ||
            !state.Outbox.TryGetValue(intent.Id, out var saved) || saved.Digest != intent.Digest ||
            saved.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown) ||
            saved.Kind != intent.Kind || saved.IssueNumber != intent.IssueNumber || saved.Generation != intent.Generation ||
            saved.TaskId != intent.TaskId || saved.Round != intent.Round || saved.Sha != intent.Sha ||
            saved.RequiredStatus != intent.RequiredStatus || intent.RequiredStatus?.Context != RequiredStatusPolicy.Context ||
            intent.Sha is not { Length: 40 } || !intent.Sha.All(Uri.IsHexDigit) ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue))
            throw new InvalidDataException("Required status is not a canonical executing intent.");
        return issue;
    }

    private static void ValidateCurrent(RepositoryState state, IssueState issue, EffectIntent intent)
    {
        if (!RequiredStatusPolicy.Matches(state, issue, intent))
            throw new InvalidDataException("Required-status eligibility changed.");
    }

    private async Task PreflightAsync(RepositoryState state, IssueState issue, EffectIntent intent, CancellationToken token)
    {
        ValidateCurrent(state, issue, intent);
        ValidatePublisher(new(publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type, clock.GetUtcNow(), []));
        await api.VerifyRepositoryAsync(token);
        using var actor = await api.GetGlobalAsync("users/" + Uri.EscapeDataString(publisher.Login), token);
        ValidateActor(actor.RootElement, publisher.ActorId, publisher.Login, publisher.NodeId, publisher.Type);
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        using var pull = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        var pr = pull.RootElement;
        if (liveIssue.RootElement.GetProperty("number").GetInt32() != issue.Number ||
            pr.GetProperty("number").GetInt32() != issue.PullRequest ||
            pr.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pr.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            state.IsolatedTestManifestDigest is not null &&
                (!pr.GetProperty("draft").GetBoolean() ||
                 pr.GetProperty("base").GetProperty("ref").GetString() != GitHubIsolatedStatusTestSettings.BaseBranch) ||
            pr.GetProperty("head").GetProperty("sha").GetString() != intent.Sha)
            throw new InvalidDataException("Required-status live head/repository mapping changed.");
        if (intent.RequiredStatus!.State == "success")
        {
            if (liveIssue.RootElement.GetProperty("state").GetString() != "open" || pr.GetProperty("state").GetString() != "open")
                throw new InvalidDataException("Closed work cannot publish success.");
            await VerifyGreenRunAsync(issue, token);
        }
    }

    private async Task VerifyGreenRunAsync(IssueState issue, CancellationToken token)
    {
        var ci = issue.Ci!;
        var boundary = ci.DispatchBoundary!;
        using var definition = await api.GetAsync("commits/" + Uri.EscapeDataString(boundary.Ref), token);
        if (!boundary.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) ||
            definition.RootElement.GetProperty("sha").GetString() != boundary.WorkflowSha)
            throw new InvalidDataException("Required-status trusted CI definition changed.");
        using var run = await api.GetAsync($"actions/runs/{ci.RunId}", token);
        var root = run.RootElement;
        if (root.GetProperty("id").GetInt64() != boundary.RunId ||
            root.GetProperty("workflow_id").GetInt64() != boundary.WorkflowId ||
            root.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            root.GetProperty("path").GetString() != boundary.WorkflowPath ||
            root.GetProperty("event").GetString() != "workflow_dispatch" ||
            root.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
            root.GetProperty("run_attempt").GetInt32() != 1 ||
            root.GetProperty("display_title").GetString() != boundary.Correlation ||
            root.GetProperty("status").GetString() != "completed" || root.GetProperty("conclusion").GetString() != "success")
            throw new InvalidDataException("Required-status trusted CI run does not prove success.");
        using var jobs = await api.GetAsync($"actions/runs/{ci.RunId}/jobs?filter=latest&per_page=100&page=1", token);
        var items = jobs.RootElement.GetProperty("jobs");
        if (jobs.RootElement.GetProperty("total_count").GetInt32() != 1 || items.GetArrayLength() != 1)
            throw new InvalidDataException("Required-status CI job evidence is ambiguous.");
        var job = items[0];
        if (job.GetProperty("id").GetInt64() <= 0 || job.GetProperty("run_id").GetInt64() != boundary.RunId ||
            job.GetProperty("run_attempt").GetInt32() != 1 || job.GetProperty("head_sha").GetString() != boundary.WorkflowSha ||
            job.GetProperty("name").GetString() != "Test " + ci.Sha ||
            job.GetProperty("status").GetString() != "completed" || job.GetProperty("conclusion").GetString() != "success")
            throw new InvalidDataException("Required-status CI job did not test the captured head.");
        foreach (var name in new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" })
        {
            var steps = job.GetProperty("steps").EnumerateArray().Where(item => item.GetProperty("name").GetString() == name).ToList();
            if (steps.Count != 1 || steps[0].GetProperty("status").GetString() != "completed" ||
                steps[0].GetProperty("conclusion").GetString() != "success")
                throw new InvalidDataException("Required-status CI steps are not verified green.");
        }
    }

    private async Task<List<JsonElement>> ReadStatusesAsync(EffectIntent intent, CancellationToken token)
    {
        using var combined = await api.GetAsync($"commits/{intent.Sha}/status", token);
        if (combined.RootElement.GetProperty("sha").GetString() != intent.Sha ||
            combined.RootElement.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            combined.RootElement.GetProperty("repository").GetProperty("full_name").GetString() != api.Repository)
            throw new InvalidDataException("Status lookup is not for the exact repository/SHA.");
        var items = await api.ListAsync($"commits/{intent.Sha}/statuses", token);
        if (items.Any(item => item.GetProperty("id").GetInt64() <= 0) ||
            items.Select(item => item.GetProperty("id").GetInt64()).Distinct().Count() != items.Count)
            throw new InvalidDataException("Status lookup identities are invalid or duplicated.");
        return items;
    }

    private long ValidateStatus(JsonElement status, EffectIntent intent, RequiredStatusBoundary boundary)
    {
        var id = status.GetProperty("id").GetInt64();
        var payload = intent.RequiredStatus!;
        if (id <= 0 || status.GetProperty("url").GetString() != $"https://api.github.com/repos/{api.Repository}/statuses/{id}" ||
            status.GetProperty("context").GetString() != payload.Context || status.GetProperty("state").GetString() != payload.State ||
            status.GetProperty("target_url").GetString() != payload.TargetUrl || status.GetProperty("description").GetString() != payload.Description ||
            status.GetProperty("created_at").GetDateTimeOffset().ToUnixTimeSeconds() < boundary.PreparedAt.ToUnixTimeSeconds() ||
            status.GetProperty("created_at").GetDateTimeOffset() > clock.GetUtcNow().AddMinutes(1))
            throw new InvalidDataException("Required-status receipt identity/payload mismatch.");
        ValidateActor(status.GetProperty("creator"), boundary.ActorId, boundary.ActorLogin, boundary.ActorNodeId, boundary.ActorType);
        return id;
    }

    private void ValidatePublisher(RequiredStatusBoundary boundary)
    {
        if (publisher.ActorId <= 0 || string.IsNullOrWhiteSpace(publisher.Login) || string.IsNullOrWhiteSpace(publisher.NodeId) ||
            publisher.Type is not ("User" or "Bot") || boundary.ActorId != publisher.ActorId ||
            boundary.ActorLogin != publisher.Login || boundary.ActorNodeId != publisher.NodeId || boundary.ActorType != publisher.Type)
            throw new InvalidDataException("Required-status publisher is not the trusted actor.");
    }

    private static void ValidateActor(JsonElement actor, long id, string login, string nodeId, string type)
    {
        if (actor.GetProperty("id").GetInt64() != id || actor.GetProperty("login").GetString() != login ||
            actor.GetProperty("node_id").GetString() != nodeId || actor.GetProperty("type").GetString() != type)
            throw new InvalidDataException("Required-status creator identity mismatch.");
    }

    private async Task SaveBoundaryAsync(EffectIntent intent, RequiredStatusBoundary boundary, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            ValidateIntent(snapshot.State, intent);
            if (boundary.StatusId is null && snapshot.State.Outbox[intent.Id].RequiredStatusBoundary is not null)
                throw new InvalidDataException("Another publisher already prepared this intent.");
            var next = new OrchestrationReducer().RecordRequiredStatusBoundary(snapshot.State, intent.Id, boundary);
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Required-status receipt publication conflict; intent remains lookup-only.");
    }
}
