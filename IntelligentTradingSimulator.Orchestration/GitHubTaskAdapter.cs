using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubExecutionApproval(
    bool ActivationApproved = false, bool UserTokenVerified = false,
    bool TaskApiAndEntitlementVerified = false, bool NoAdditionalChargesVerified = false)
{
    public bool AllowsTasks => ActivationApproved && UserTokenVerified &&
        TaskApiAndEntitlementVerified && NoAdditionalChargesVerified;
}

public sealed record TaskFinalHeadEvidence(string ExternalTaskId, List<string> SessionIds, int PullRequest, string Sha);

public interface ITaskFinalHeadSource
{
    // Optional stronger evidence; when present it must match the bounded task/session/PR head snapshot.
    Task<TaskFinalHeadEvidence?> ReadAsync(string externalTaskId, CancellationToken token);
}

public interface ICandidateShaValidator
{
    Task ValidateCandidateShaAsync(RepositoryState state, IssueState issue, CancellationToken token);
}

/// <summary>Documented agent-task API. Lost launch responses without an ID remain unknown, never re-created.</summary>
public sealed class GitHubTaskAdapter(
    GitHubRepositoryApi api, IStateStore store, string baseBranch, long taskCreatorId,
    GitHubExecutionApproval? approval = null, ITaskFinalHeadSource? finalHeadSource = null,
    GitHubRepositoryApi? taskApi = null) :
    IVerifiedAgentEffectAdapter, ICandidateShaValidator
{
    public bool AgentEffectsAuthorized => approval?.AllowsTasks == true;
    private readonly GitHubRepositoryApi taskApi = taskApi ?? api;
    private string TaskPath => $"agents/repos/{api.Repository}/tasks";

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (!AgentEffectsAuthorized) throw new InvalidOperationException("Task activation prerequisites are not verified.");
        var state = (await store.LoadAsync(token)).State;
        if (state.Repository != api.Repository || !state.Outbox.TryGetValue(intent.Id, out var persisted) ||
            persisted.Status != DeliveryStatus.Executing || persisted.Digest != intent.Digest ||
            intent.Kind != EffectKind.LaunchTask || state.AdmittedIssue != intent.IssueNumber ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue) || issue.Generation != intent.Generation ||
            issue.Paused || issue.ReactivationRequired || !issue.ActiveAuthorized || issue.ActiveTaskId != intent.TaskId ||
            !issue.Tasks.TryGetValue(intent.TaskId!, out var task) || task.Status != TaskStatus.Executing ||
            string.IsNullOrWhiteSpace(intent.TaskPrompt) || taskCreatorId <= 0 || string.IsNullOrWhiteSpace(baseBranch))
            throw new InvalidDataException("Task launch does not match a persisted executing intent.");
        await api.VerifyRepositoryAsync(token);
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        if (liveIssue.RootElement.GetProperty("state").GetString() != "open" ||
            liveIssue.RootElement.TryGetProperty("pull_request", out _))
            throw new InvalidDataException("Coding requires an open Issue.");
        string? headRef = null;
        if (task.LaunchSource == TaskLaunchSource.UnmappedIssue)
        {
            if (task.Kind != TaskKind.Coding || task.ParentTaskId is not null ||
                issue.PullRequest is not null || issue.HeadSha is not null || task.SourceSha is not null || intent.Sha is not null ||
                !liveIssue.RootElement.GetProperty("labels").EnumerateArray()
                    .Any(label => label.GetProperty("name").GetString() == "agent:ready"))
                throw new InvalidDataException("Initial launch requires explicit unmapped readiness.");
        }
        else if (task.LaunchSource == TaskLaunchSource.PullRequestHead && issue.PullRequest is { } prNumber)
        {
            using var pr = await api.GetAsync($"pulls/{prNumber}", token);
            ValidatePull(pr.RootElement, prNumber);
            var sha = pr.RootElement.GetProperty("head").GetProperty("sha").GetString();
            if (sha != task.SourceSha || sha != intent.Sha || sha != issue.HeadSha)
                throw new InvalidDataException("Repair source head changed before launch.");
            headRef = pr.RootElement.GetProperty("head").GetProperty("ref").GetString();
        }
        else throw new InvalidDataException("Invalid task launch source.");
        using var result = await taskApi.PostGlobalAsync(TaskPath,
            new { prompt = intent.TaskPrompt, base_ref = baseBranch, head_ref = headRef, create_pull_request = headRef is null }, token);
        var id = ValidateTask(result.RootElement, null);
        return new(DeliveryStatus.Succeeded, id);
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        if (state.Repository != api.Repository || intent.Kind != EffectKind.LaunchTask ||
            !state.Outbox.TryGetValue(intent.Id, out var persisted) || persisted.Digest != intent.Digest ||
            persisted.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown) ||
            persisted.TaskId != intent.TaskId || persisted.ExternalId != intent.ExternalId ||
            persisted.Generation != intent.Generation || persisted.IssueNumber != intent.IssueNumber ||
            !state.Issues.TryGetValue(persisted.IssueNumber, out var issue) || issue.Generation != persisted.Generation ||
            state.AdmittedIssue != issue.Number || issue.ActiveTaskId != persisted.TaskId ||
            !issue.Tasks.TryGetValue(persisted.TaskId!, out var identity) ||
            identity.Id != persisted.TaskId || identity.Round != persisted.Round ||
            identity.Status is not (TaskStatus.Executing or TaskStatus.Unknown or TaskStatus.Running) ||
            identity.ExternalId is not null && identity.ExternalId != persisted.ExternalId)
            throw new InvalidDataException("Task recovery does not match a canonical uncertain launch.");
        // The API has no caller idempotency key. Prompt/list similarity is not a unique launch identity.
        if (string.IsNullOrWhiteSpace(persisted.ExternalId)) return new(DeliveryStatus.Unknown);
        await api.VerifyRepositoryAsync(token);
        using var task = await taskApi.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(persisted.ExternalId), token);
        return new(DeliveryStatus.Succeeded, ValidateTask(task.RootElement, persisted.ExternalId));
    }

    public async Task<OrchestrationEvent?> ReadMappingEventAsync(RepositoryState state, IssueState issue,
        CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.ActiveTaskId is not { } taskId ||
            !issue.Tasks.TryGetValue(taskId, out var task) || !task.LaunchConfirmed ||
            task.Status != TaskStatus.Running || string.IsNullOrWhiteSpace(task.ExternalId))
            return null;
        var launch = TaskTerminalRecovery.RequireSavedLaunch(state, issue, taskId);
        if (launch.Status != DeliveryStatus.Succeeded || launch.ExternalId != task.ExternalId)
            throw new InvalidDataException("PR mapping requires a confirmed canonical launch receipt.");
        await api.VerifyRepositoryAsync(token);
        using var response = await taskApi.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(task.ExternalId), token);
        var root = response.RootElement;
        var stage = "task-identity";
        try
        {
            ValidateTask(root, task.ExternalId);
            var inProgress = root.GetProperty("state").GetString() is "queued" or "in_progress";
            stage = "artifact-shape";
            if (!root.TryGetProperty("artifacts", out var artifacts))
            {
                if (inProgress) return null;
                throw new InvalidDataException("Task artifacts are unavailable.");
            }
            var items = artifacts.EnumerateArray().ToList();
            if (items.Count == 0 && inProgress) return null;
            if (items.Count > 100) throw new InvalidDataException("Task artifact capacity exceeded.");
            stage = "artifact-types";
            var branches = items.Where(item => item.GetProperty("provider").GetString() == "github" &&
                item.GetProperty("type").GetString() == "branch").ToList();
            var pulls = items.Where(item => item.GetProperty("provider").GetString() == "github" &&
                item.GetProperty("type").GetString() == "pull").ToList();
            if (branches.Count > 1 || pulls.Count > 1 || branches.Count + pulls.Count == 0 ||
                branches.Count + pulls.Count != items.Count)
                throw new InvalidDataException("Task artifact mapping is incomplete or contradictory.");
            int number;
            string? headRef = null;
            if (branches.Count == 1)
            {
                stage = "branch-refs";
                var data = branches[0].GetProperty("data");
                headRef = data.GetProperty("head_ref").GetString();
                var baseRef = data.GetProperty("base_ref").GetString();
                if (string.IsNullOrWhiteSpace(headRef) || baseRef != baseBranch)
                    throw new InvalidDataException("Task artifact refs do not match the configured repository.");
                stage = "branch-pr-lookup";
                var owner = api.Repository.Split('/')[0];
                var matches = await api.ListAsync($"pulls?state=open&head={Uri.EscapeDataString(owner + ":" + headRef)}&base={Uri.EscapeDataString(baseRef)}", token);
                if (matches.Count == 0 && inProgress && pulls.Count == 0) return null;
                if (matches.Count != 1) throw new InvalidDataException("Task artifact has no unique open PR.");
                number = matches[0].GetProperty("number").GetInt32();
                stage = "branch-pull-consistency";
                if (pulls.Count == 1 && await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token) != number)
                    throw new InvalidDataException("Task branch and pull artifacts contradict each other.");
            }
            else
            {
                stage = "pull-artifact-resolution";
                number = await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token);
            }
            stage = "saved-pr-consistency";
            if (issue.PullRequest is { } mapped && mapped != number)
                throw new InvalidDataException("Task artifact changed the mapped PR.");
            stage = "live-pr-validation";
            using var pull = await api.GetAsync($"pulls/{number}", token);
            ValidatePull(pull.RootElement, number);
            if (headRef is not null && pull.RootElement.GetProperty("head").GetProperty("ref").GetString() != headRef)
                throw new InvalidDataException("Task artifact head does not match the live PR.");
            var sha = pull.RootElement.GetProperty("head").GetProperty("sha").GetString()!;
            if (issue.PullRequest == number && issue.HeadSha == sha) return null;
            stage = "task-updated-at";
            var at = root.GetProperty("updated_at").GetDateTimeOffset();
            return new($"task-pr-mapped:{api.RepositoryId}:{issue.Generation}:{task.ExternalId}:{number}:{sha}:{at:O}",
                api.Repository, issue.Number, state.Generation, issue.Generation,
                new("github-task-api", ActorKind.TaskService), at, new TaskPrMapped(taskId, number, sha));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            var at = root.TryGetProperty("updated_at", out var updated) && updated.TryGetDateTimeOffset(out var value)
                ? value : issue.ReadyAt ?? DateTimeOffset.UnixEpoch;
            var failureCode = $"mapping/{stage}/{exception.GetType().Name}";
            return new($"task-pr-mapping-stopped:v2:{api.RepositoryId}:{issue.Generation}:{task.ExternalId}:{failureCode}:{StateJson.Digest(root.GetRawText())}",
                api.Repository, issue.Number, state.Generation, issue.Generation,
                new("github-task-api", ActorKind.TaskService), at,
                new TaskStopped(taskId,
                    $"Task PR mapping is unsupported or contradictory; code={failureCode}; admission retained."));
        }
    }

    public async Task<OrchestrationEvent> ReadTerminalEventAsync(RepositoryState state, IssueState issue,
        DateTimeOffset observedAt, CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.ActiveTaskId is not { } taskId || taskCreatorId <= 0)
            throw new InvalidDataException("Recovery repository or saved task identity is missing.");
        var launch = TaskTerminalRecovery.RequireSavedLaunch(state, issue, taskId);
        await api.VerifyRepositoryAsync(token);
        using var result = await taskApi.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(launch.ExternalId!), token);
        try
        {
            var root = result.RootElement;
            ValidateTask(root, launch.ExternalId);
            var sessions = root.GetProperty("sessions").EnumerateArray().Select(session =>
                new TaskSessionTerminalEvidence(session.GetProperty("id").GetString()!,
                    session.GetProperty("task_id").GetString()!,
                    session.GetProperty("repository").GetProperty("id").GetInt64(),
                    session.GetProperty("state").GetString()!,
                    session.GetProperty("completed_at").GetDateTimeOffset())).ToList();
            var evidence = new TaskTerminalEvidence(launch.Id, launch.ExternalId!, api.Repository,
                api.RepositoryId, taskCreatorId, root.GetProperty("state").GetString()!,
                root.GetProperty("session_count").GetInt32(), sessions,
                root.GetProperty("updated_at").GetDateTimeOffset(), observedAt,
                StateJson.Digest(root.GetRawText()));
            TaskTerminalRecovery.ValidateEvidence(evidence, state, launch);
            return new($"task-ended:{api.RepositoryId}:{issue.Generation}:{launch.ExternalId}:{evidence.ResponseDigest}:{observedAt:O}",
                api.Repository, issue.Number, state.Generation, issue.Generation,
                new("github-task-api", ActorKind.TaskService), observedAt, new TaskTerminalObserved(taskId, evidence));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Unsupported task recovery response; admission remains blocked.", exception);
        }
    }

    public async Task<OrchestrationEvent?> ReadEventAsync(RepositoryState state, IssueState issue, CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.ActiveTaskId is not { } taskId ||
            !issue.Tasks.TryGetValue(taskId, out var task) || !task.LaunchConfirmed ||
            task.Status is not (TaskStatus.Running or TaskStatus.AwaitingHeadConfirmation) ||
            string.IsNullOrWhiteSpace(task.ExternalId))
            return null;
        using var result = await taskApi.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(task.ExternalId), token);
        var root = result.RootElement;
        try
        {
            ValidateTask(root, task.ExternalId);
            var remoteState = root.GetProperty("state").GetString();
            if (remoteState is "queued" or "in_progress") return null;
            if (remoteState != "completed") return Stopped("Tracked task requires human reconciliation.");
            var sessions = root.GetProperty("sessions").EnumerateArray().ToList();
            if (sessions.Count == 0 || sessions.Count > 100 || root.GetProperty("session_count").GetInt32() != sessions.Count ||
                sessions.Any(session => session.GetProperty("state").GetString() != "completed" ||
                    session.GetProperty("task_id").GetString() != task.ExternalId ||
                    session.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId))
                return Stopped("Task session completion is incomplete or contradictory.");
            var (number, sha) = await ReadTaskPullMappingAsync(root, issue, token);
            if (issue.PullRequest is { } mapped && mapped != number)
                return Stopped("Task artifact changed the mapped PR.");
            var evidence = finalHeadSource is null ? null : await finalHeadSource.ReadAsync(task.ExternalId, token);
            if (evidence is not null && (evidence.SessionIds is null || evidence.ExternalTaskId != task.ExternalId ||
                evidence.PullRequest != number || evidence.Sha != sha || evidence.SessionIds.Count != sessions.Count ||
                !evidence.SessionIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
                    sessions.Select(session => session.GetProperty("id").GetString()!)
                        .OrderBy(id => id, StringComparer.Ordinal))))
                return Stopped("Trusted task-head evidence contradicts the completed task snapshot.");
            var at = root.GetProperty("updated_at").GetDateTimeOffset();
            var candidate = new CandidateShaEvidence(TaskTerminalRecovery.CandidateShaEvidenceVersion,
                TaskTerminalRecovery.CandidateShaPolicy, task.Id, task.ExternalId,
                sessions.Select(session => session.GetProperty("id").GetString()!).ToList(), number, sha);
            return new($"task-completed:{api.RepositoryId}:{issue.Generation}:{task.ExternalId}", api.Repository,
                issue.Number, state.Generation, issue.Generation, new("github-task-api", ActorKind.TaskService),
                at, new TaskCompleted(task.Id, number, sha, candidate));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Stopped("Task API identity or artifact response is unsupported.");
        }

        OrchestrationEvent Stopped(string reason) => new(
            $"task-stopped:{api.RepositoryId}:{issue.Generation}:{task.ExternalId}", api.Repository, issue.Number,
            state.Generation, issue.Generation, new("github-task-api", ActorKind.TaskService),
            taskCompletionTime(), new TaskStopped(task.Id, reason));
        DateTimeOffset taskCompletionTime() => root.TryGetProperty("updated_at", out var value) &&
            value.TryGetDateTimeOffset(out var at) ? at : issue.ReadyAt ?? DateTimeOffset.UnixEpoch;
    }

    public async Task ValidateCandidateShaAsync(RepositoryState state, IssueState issue, CancellationToken token)
    {
        if (state.Repository != api.Repository || !state.Issues.TryGetValue(issue.Number, out var canonical) ||
            canonical.Generation != issue.Generation || canonical.ActiveTaskId is not { } taskId ||
            !canonical.Tasks.TryGetValue(taskId, out var task) || task.Status != TaskStatus.Completed ||
            !task.LaunchConfirmed || task.FinalSha is not { } sha || canonical.PullRequest is not { } pullRequest)
            throw new InvalidDataException("Candidate SHA does not match a completed canonical task.");
        var evidence = task.CandidateShaEvidence;
        TaskTerminalRecovery.ValidateCandidateSha(evidence, task, pullRequest, sha);
        var launches = state.Outbox.Values.Where(intent => intent.Kind == EffectKind.LaunchTask &&
            intent.IssueNumber == canonical.Number && intent.Generation == canonical.Generation &&
            intent.TaskId == taskId).ToList();
        if (launches.Count != 1 || launches[0].Status != DeliveryStatus.Succeeded ||
            launches[0].ExternalId != evidence!.ExternalTaskId || task.ExternalId != evidence.ExternalTaskId)
            throw new InvalidDataException("Candidate SHA has no exact completed task launch receipt.");

        await api.VerifyRepositoryAsync(token);
        using var remote = await taskApi.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(evidence.ExternalTaskId), token);
        var root = remote.RootElement;
        ValidateTask(root, evidence.ExternalTaskId);
        var sessions = root.GetProperty("sessions").EnumerateArray().ToList();
        var sessionIds = sessions.Select(session =>
        {
            if (session.GetProperty("state").GetString() != "completed" ||
                session.GetProperty("task_id").GetString() != evidence.ExternalTaskId ||
                session.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId)
                throw new InvalidDataException("Candidate task session is no longer terminal or mapped.");
            return session.GetProperty("id").GetString()!;
        }).OrderBy(id => id, StringComparer.Ordinal);
        if (root.GetProperty("state").GetString() != "completed" || sessions.Count is < 1 or > 100 ||
            root.GetProperty("session_count").GetInt32() != sessions.Count ||
            !sessionIds.SequenceEqual(evidence.SessionIds.OrderBy(id => id, StringComparer.Ordinal)))
            throw new InvalidDataException("Candidate task/session identity changed after its snapshot.");
        var (mappedPullRequest, liveSha) = await ReadTaskPullMappingAsync(root, canonical, token);
        if (mappedPullRequest != evidence.PullRequest || liveSha != evidence.ObservedHeadSha ||
            canonical.HeadSha != evidence.ObservedHeadSha)
            throw new InvalidDataException("Candidate task mapping or PR head changed after its snapshot.");
    }

    private async Task<(int PullRequest, string Sha)> ReadTaskPullMappingAsync(JsonElement root, IssueState issue,
        CancellationToken token)
    {
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToList();
        if (artifacts.Count is < 1 or > 100)
            throw new InvalidDataException("Task artifacts are missing or exceed capacity.");
        var branches = artifacts.Where(artifact => artifact.GetProperty("provider").GetString() == "github" &&
            artifact.GetProperty("type").GetString() == "branch").ToList();
        var pulls = artifacts.Where(artifact => artifact.GetProperty("provider").GetString() == "github" &&
            artifact.GetProperty("type").GetString() == "pull").ToList();
        if (branches.Count > 1 || pulls.Count > 1 || branches.Count + pulls.Count != artifacts.Count ||
            branches.Count + pulls.Count == 0)
            throw new InvalidDataException("Task artifact mapping is incomplete or contradictory.");
        int number;
        string? headRef = null;
        if (branches.Count == 1)
        {
            var data = branches[0].GetProperty("data");
            headRef = data.GetProperty("head_ref").GetString();
            var baseRef = data.GetProperty("base_ref").GetString();
            if (string.IsNullOrWhiteSpace(headRef) || baseRef != baseBranch)
                throw new InvalidDataException("Task artifact refs do not match the configured repository.");
            var owner = api.Repository.Split('/')[0];
            var matches = await api.ListAsync($"pulls?state=open&head={Uri.EscapeDataString(owner + ":" + headRef)}&base={Uri.EscapeDataString(baseRef)}", token);
            if (matches.Count != 1)
                throw new InvalidDataException("Task artifact has no unique open PR.");
            number = matches[0].GetProperty("number").GetInt32();
            if (pulls.Count == 1 && await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token) != number)
                throw new InvalidDataException("Task branch and pull artifacts contradict each other.");
        }
        else number = await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token);
        if (issue.PullRequest is { } mapped && mapped != number)
            throw new InvalidDataException("Task artifact changed the mapped PR.");
        using var pull = await api.GetAsync($"pulls/{number}", token);
        ValidatePull(pull.RootElement, number);
        if (headRef is not null && pull.RootElement.GetProperty("head").GetProperty("ref").GetString() != headRef)
            throw new InvalidDataException("Task artifact head does not match the live PR.");
        return (number, pull.RootElement.GetProperty("head").GetProperty("sha").GetString()!);
    }

    public async Task<LiveObservation> ObserveAsync(RepositoryState state, DateTimeOffset now, CancellationToken token)
    {
        var live = await api.ObserveAsync(state, now, token);
        foreach (var issue in state.Issues.Values)
        {
            var message = await ReadEventAsync(state, issue, token);
            if (message?.Data is not TaskCompleted completed) continue;
            live.Issues[issue.Number].PullRequest = completed.PullRequest;
            live.Issues[issue.Number].HeadSha = completed.Sha;
            live.Issues[issue.Number].CompletedTaskId = completed.TaskId;
        }
        return live;
    }

    private string ValidateTask(JsonElement task, string? expectedId)
    {
        var id = task.GetProperty("id").GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || expectedId is not null && id != expectedId ||
            task.GetProperty("repository").GetProperty("id").GetInt64() != api.RepositoryId ||
            task.GetProperty("creator").GetProperty("id").GetInt64() != taskCreatorId ||
            task.GetProperty("state").GetString() is not ("queued" or "in_progress" or "completed" or "failed" or
                "idle" or "waiting_for_user" or "timed_out" or "cancelled"))
            throw new InvalidDataException("Task API identity or state mismatch.");
        return id;
    }

    private void ValidatePull(JsonElement pull, int number)
    {
        var sha = pull.GetProperty("head").GetProperty("sha").GetString();
        if (number <= 0 || pull.GetProperty("number").GetInt32() != number ||
            pull.GetProperty("state").GetString() != "open" ||
            pull.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.GetProperty("base").GetProperty("ref").GetString() != baseBranch ||
            string.IsNullOrWhiteSpace(pull.GetProperty("head").GetProperty("ref").GetString()) ||
            sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("Task PR mapping is unsupported or changed.");
    }
}
