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
    // Trusted host integration must establish the final commit from the task, not today's live PR head.
    Task<TaskFinalHeadEvidence?> ReadAsync(string externalTaskId, CancellationToken token);
}

/// <summary>Documented agent-task API. Lost launch responses without an ID remain unknown, never re-created.</summary>
public sealed class GitHubTaskAdapter(
    GitHubRepositoryApi api, IStateStore store, string baseBranch, long taskCreatorId,
    GitHubExecutionApproval? approval = null, ITaskFinalHeadSource? finalHeadSource = null) : IVerifiedAgentEffectAdapter
{
    public bool AgentEffectsAuthorized => approval?.AllowsTasks == true;
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
        using var result = await api.PostGlobalAsync(TaskPath,
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
        using var task = await api.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(persisted.ExternalId), token);
        return new(DeliveryStatus.Succeeded, ValidateTask(task.RootElement, persisted.ExternalId));
    }

    public async Task<OrchestrationEvent?> ReadEventAsync(RepositoryState state, IssueState issue, CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.ActiveTaskId is not { } taskId ||
            !issue.Tasks.TryGetValue(taskId, out var task) || !task.LaunchConfirmed ||
            task.Status is not (TaskStatus.Running or TaskStatus.AwaitingHeadConfirmation) ||
            string.IsNullOrWhiteSpace(task.ExternalId))
            return null;
        using var result = await api.GetGlobalAsync(TaskPath + "/" + Uri.EscapeDataString(task.ExternalId), token);
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
                    session.TryGetProperty("task_id", out var sessionTask) && sessionTask.GetString() != task.ExternalId ||
                    session.TryGetProperty("repository", out var repository) &&
                    repository.GetProperty("id").GetInt64() != api.RepositoryId))
                return Stopped("Task session completion is incomplete or contradictory.");
            var artifacts = root.GetProperty("artifacts").EnumerateArray().ToList();
            if (artifacts.Count > 100) return Stopped("Task artifact capacity exceeded.");
            var branches = artifacts.Where(artifact =>
                artifact.GetProperty("provider").GetString() == "github" &&
                artifact.GetProperty("type").GetString() == "branch").ToList();
            var pulls = artifacts.Where(artifact =>
                artifact.GetProperty("provider").GetString() == "github" &&
                artifact.GetProperty("type").GetString() == "pull").ToList();
            if (branches.Count > 1 || pulls.Count > 1 || branches.Count + pulls.Count == 0)
                return Stopped("Task artifact mapping requires human reconciliation.");
            int number;
            string? head = null;
            if (branches.Count == 1)
            {
                var data = branches[0].GetProperty("data");
                head = data.GetProperty("head_ref").GetString();
                var baseRef = data.GetProperty("base_ref").GetString();
                if (string.IsNullOrWhiteSpace(head) || baseRef != baseBranch)
                    return Stopped("Task artifact refs do not match the configured repository.");
                var owner = api.Repository.Split('/')[0];
                var matches = await api.ListAsync($"pulls?state=open&head={Uri.EscapeDataString(owner + ":" + head)}&base={Uri.EscapeDataString(baseRef)}", token);
                if (matches.Count != 1) return Stopped("Task artifact has no unique open PR.");
                number = matches[0].GetProperty("number").GetInt32();
                if (pulls.Count == 1 && await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token) != number)
                    return Stopped("Task branch and pull artifacts contradict each other.");
            }
            else number = await api.ResolvePullArtifactAsync(pulls[0].GetProperty("data"), token);
            if (issue.PullRequest is { } mapped && mapped != number)
                return Stopped("Task artifact changed the mapped PR.");
            using var pull = await api.GetAsync($"pulls/{number}", token);
            ValidatePull(pull.RootElement, number);
            if (head is not null && pull.RootElement.GetProperty("head").GetProperty("ref").GetString() != head)
                return Stopped("Task artifact head does not match the live PR.");
            var sha = pull.RootElement.GetProperty("head").GetProperty("sha").GetString()!;
            var evidence = finalHeadSource is null ? null : await finalHeadSource.ReadAsync(task.ExternalId, token);
            if (evidence is null || evidence.SessionIds is null || evidence.ExternalTaskId != task.ExternalId || evidence.PullRequest != number ||
                evidence.Sha != sha || evidence.SessionIds.Count != sessions.Count ||
                !evidence.SessionIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(
                    sessions.Select(session => session.GetProperty("id").GetString()!)
                        .OrderBy(id => id, StringComparer.Ordinal)))
                return Stopped("Completed task final commit lacks authoritative task-bound evidence.");
            // REST branch artifacts alone cannot establish this SHA; the trusted final-head source must.
            var at = root.GetProperty("updated_at").GetDateTimeOffset();
            return new($"task-completed:{api.RepositoryId}:{issue.Generation}:{task.ExternalId}", api.Repository,
                issue.Number, state.Generation, issue.Generation, new("github-task-api", ActorKind.TaskService),
                at, new TaskCompleted(task.Id, number, sha));
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
