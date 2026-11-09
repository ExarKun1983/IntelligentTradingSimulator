namespace IntelligentTradingSimulator.Orchestration;

public static class TaskTerminalRecovery
{
    public const string Documentation = "https://docs.github.com/en/rest/agent-tasks/agent-tasks#get-a-task-by-repo";
    public const int CandidateShaEvidenceVersion = 1;
    public const string CandidateShaPolicy = "bounded-candidate-sha-v1";
    public const string Limitation =
        "The preview API documents session state and completed_at, not irreversible task terminality. " +
        "Evidence records an ended-session snapshot only; operator resolution ends local tracking without " +
        "asserting successful coding, a final commit, or that the remote task cannot be resumed.";
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromMinutes(5);

    public static bool IsEndedState(string state) => state is "completed" or "failed" or "cancelled" or "timed_out";

    public static EffectIntent RequireSavedLaunch(RepositoryState state, IssueState issue, string taskId)
    {
        if (!state.Issues.TryGetValue(issue.Number, out var canonical) ||
            canonical.Generation != issue.Generation || state.AdmittedIssue != canonical.Number ||
            canonical.ActiveTaskId != taskId || !canonical.Tasks.TryGetValue(taskId, out var task) ||
            task.Id != taskId || task.Status is not (TaskStatus.Executing or TaskStatus.Unknown or
                TaskStatus.Running or TaskStatus.AwaitingHeadConfirmation) || task.OperatorResolution is not null)
            throw new InvalidDataException("Recovery requires a canonical active or uncertain saved task.");
        var launches = state.Outbox.Values.Where(intent => intent.Kind == EffectKind.LaunchTask &&
            intent.IssueNumber == canonical.Number && intent.Generation == canonical.Generation &&
            intent.TaskId == taskId).ToList();
        if (launches.Count != 1) throw new InvalidDataException("Recovery requires exactly one saved launch receipt.");
        var launch = launches[0];
        if (!state.Outbox.TryGetValue(launch.Id, out var indexed) || !ReferenceEquals(indexed, launch) ||
            launch.Round != task.Round || string.IsNullOrWhiteSpace(launch.Digest) ||
            launch.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown or DeliveryStatus.Succeeded) ||
            string.IsNullOrWhiteSpace(launch.ExternalId) || launch.ExternalId.Length > 256 ||
            task.ExternalId is not null && task.ExternalId != launch.ExternalId ||
            task.LaunchConfirmed && task.ExternalId != launch.ExternalId)
            throw new InvalidDataException("Recovery requires an exact saved remote identity; no task search is supported.");
        return launch;
    }

    public static void ValidateEvidence(TaskTerminalEvidence evidence, RepositoryState state, EffectIntent launch)
    {
        if (evidence is null || evidence.Repository != state.Repository ||
            evidence.LaunchIntentId != launch.Id || evidence.ExternalTaskId != launch.ExternalId ||
            evidence.RepositoryId <= 0 || evidence.CreatorId <= 0 || !IsEndedState(evidence.State) ||
            evidence.Sessions is null || evidence.SessionCount is < 1 or > 100 ||
            evidence.Sessions.Count != evidence.SessionCount ||
            evidence.Sessions.Any(session => session is null || string.IsNullOrWhiteSpace(session.Id) ||
                session.Id.Length > 256 || session.ExternalTaskId != evidence.ExternalTaskId ||
                session.RepositoryId != evidence.RepositoryId || !IsEndedState(session.State) ||
                session.CompletedAt < launch.CreatedAt || session.CompletedAt > evidence.UpdatedAt) ||
            evidence.Sessions.Select(session => session.Id).Distinct(StringComparer.Ordinal).Count() != evidence.SessionCount ||
            evidence.State == "completed" && evidence.Sessions.Any(session => session.State != "completed") ||
            !evidence.Sessions.Any(session => session.State == evidence.State) ||
            evidence.UpdatedAt < launch.CreatedAt || evidence.UpdatedAt > evidence.ObservedAt ||
            evidence.ResponseDigest is not { Length: 64 } || !evidence.ResponseDigest.All(Uri.IsHexDigit))
            throw new InvalidDataException("Task/session ended evidence is incomplete or contradictory.");
    }

    public static void ValidateCandidateSha(CandidateShaEvidence? evidence, TaskIdentity task,
        int pullRequest, string sha)
    {
        if (evidence is null || evidence.Version != CandidateShaEvidenceVersion ||
            evidence.Policy != CandidateShaPolicy || evidence.TaskId != task.Id ||
            string.IsNullOrWhiteSpace(task.ExternalId) || evidence.ExternalTaskId != task.ExternalId ||
            evidence.PullRequest != pullRequest || evidence.ObservedHeadSha != sha ||
            sha is not { Length: 40 } || !sha.All(Uri.IsHexDigit) ||
            evidence.SessionIds is null || evidence.SessionIds.Count is < 1 or > 100 ||
            evidence.SessionIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 256) ||
            evidence.SessionIds.Distinct(StringComparer.Ordinal).Count() != evidence.SessionIds.Count)
            throw new InvalidDataException("Candidate SHA evidence is incomplete or mismatched.");
    }
}
