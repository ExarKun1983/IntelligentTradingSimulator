using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public enum NotificationKind { Ignore, Readiness, WithdrawReadiness, Projection, Closed, Reopened, Resume, Pause, Reactivate }

public sealed record ActionsNotification(
    NotificationKind Kind, string SourceId, long RepositoryId, long IssueId, int IssueNumber,
    long ActorId, string? Label, string? Reason);

public sealed record IngressEvidence(
    string Repository, long RepositoryId, long ActorId, string ActorType, string Permission,
    long IssueId, int IssueNumber, bool IssueOpen, bool Ready, bool IsPullRequest,
    IReadOnlySet<long> ProjectionActorIds);

/// <summary>Validates notifications; API permission/live-Issue evidence must be supplied by a trusted observer.</summary>
public static class GitHubActionsIngress
{
    private static readonly HashSet<string> StageLabels =
        ["agent:working", "agent:ci", "agent:ai-review", "agent:review", "agent:blocked"];

    public static ActionsNotification Parse(
        JsonElement payload, string eventName, string runId, IngressEvidence evidence)
    {
        if (!long.TryParse(runId, out var run) || run <= 0)
            throw new InvalidDataException("A stable Actions run ID is required.");
        var repository = payload.GetProperty("repository");
        if (repository.GetProperty("id").GetInt64() != evidence.RepositoryId ||
            repository.GetProperty("full_name").GetString() != evidence.Repository)
            throw new InvalidDataException("Repository identity mismatch.");
        var sender = payload.GetProperty("sender");
        var actorId = sender.GetProperty("id").GetInt64();
        var actorType = sender.GetProperty("type").GetString();
        if (actorId <= 0 || actorId != evidence.ActorId || actorType != evidence.ActorType)
            throw new InvalidDataException("Actor identity mismatch.");
        var source = $"actions:{evidence.RepositoryId}:{runId}";
        ActionsNotification Result(NotificationKind kind, string? label = null, string? reason = null) =>
            new(kind, source, evidence.RepositoryId, evidence.IssueId, evidence.IssueNumber,
                actorId, label, reason);
        bool AuthorizedHuman() => actorType == "User" &&
            evidence.Permission is "admin" or "maintain" or "write";

        if (eventName == "workflow_dispatch")
        {
            if (!AuthorizedHuman())
                throw new InvalidDataException("Recovery requires an authorized human.");
            var inputs = payload.GetProperty("inputs");
            if (!int.TryParse(inputs.GetProperty("issue").GetString(), out var issue) ||
                issue != evidence.IssueNumber || evidence.IsPullRequest)
                throw new InvalidDataException("Recovery Issue identity mismatch.");
            var operation = inputs.GetProperty("operation").GetString();
            var kind = operation switch
            {
                "resume" => NotificationKind.Resume,
                "pause" => NotificationKind.Pause,
                "reactivate" => NotificationKind.Reactivate,
                _ => throw new InvalidDataException("Only guarded recovery operations are supported.")
            };
            var reason = inputs.TryGetProperty("reason", out var value) ? value.GetString() : null;
            if (kind is NotificationKind.Pause or NotificationKind.Reactivate &&
                string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException("Pause/reactivation requires a reason.");
            return Result(kind, reason: reason);
        }

        if (eventName != "issues")
            throw new InvalidDataException("Unsupported notification type.");
        var issuePayload = payload.GetProperty("issue");
        if (issuePayload.GetProperty("id").GetInt64() != evidence.IssueId ||
            issuePayload.GetProperty("number").GetInt32() != evidence.IssueNumber ||
            issuePayload.TryGetProperty("pull_request", out _) || evidence.IsPullRequest)
            throw new InvalidDataException("Issue identity mismatch.");
        var action = payload.GetProperty("action").GetString();
        if (action is "closed" or "reopened")
            return Result(action == "closed" ? NotificationKind.Closed : NotificationKind.Reopened);
        if (action is not ("labeled" or "unlabeled"))
            throw new InvalidDataException("Unsupported Issue action.");
        var label = payload.GetProperty("label").GetProperty("name").GetString();
        if (label != "agent:ready" && (label is null || !StageLabels.Contains(label)))
            return Result(NotificationKind.Ignore, label);

        // A projection notification never becomes a readiness/reactivation command, even from a privileged actor.
        if (evidence.ProjectionActorIds.Contains(actorId))
            return Result(NotificationKind.Ignore, label);
        if (!AuthorizedHuman())
            throw new InvalidDataException("Lifecycle label commands require an authorized human.");
        if (label != "agent:ready")
            return Result(NotificationKind.Projection, label);
        if (!evidence.IssueOpen || !evidence.Ready)
            return Result(NotificationKind.WithdrawReadiness, label);
        // A delayed removal is not a new opt-in. The reconciler must observe a fresh authorized ready action.
        return Result(action == "labeled" ? NotificationKind.Readiness : NotificationKind.Ignore, label);
    }

}
