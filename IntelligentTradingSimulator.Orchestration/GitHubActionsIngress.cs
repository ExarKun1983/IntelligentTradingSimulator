using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public enum NotificationKind { Ignore, Readiness, WithdrawReadiness, Projection, Closed, Reopened, Resume, Pause, Reactivate, ResolveTask, ClassifyReview, RequestChanges, AbandonUncertainReview, AbandonUncertainTaskLaunch }

public sealed record ActionsNotification(
    NotificationKind Kind, string SourceId, long RepositoryId, long IssueId, int IssueNumber,
    long ActorId, string? Label, string? Reason);

public sealed record IngressEvidence(
    string Repository, long RepositoryId, long ActorId, string ActorType, string Permission,
    long IssueId, int IssueNumber, bool IssueOpen, bool Ready, bool IsPullRequest,
    HashSet<long> ProjectionActorIds);

/// <summary>Validates notifications; API permission/live-Issue evidence must be supplied by a trusted observer.</summary>
public static class GitHubActionsIngress
{
    private static readonly HashSet<string> StageLabels =
        ["agent:working", "agent:ci", "agent:ai-review", "agent:review", "agent:blocked"];

    public static OrchestrationEvent? PrepareEvent(
        JsonElement payload, string eventName, string runId, IngressEvidence evidence,
        long repositoryGeneration, long issueGeneration, DateTimeOffset occurredAt,
        int? maxRepairTasks = null)
    {
        if (maxRepairTasks is < 1 or > 8)
            throw new InvalidDataException("Repair-task test cap is outside the supported range.");
        var notification = Parse(payload, eventName, runId, evidence);
        if (notification.Kind == NotificationKind.Ignore) return null;
        if (repositoryGeneration <= 0 || issueGeneration <= 0 ||
            evidence.ActorType != "User" || evidence.Permission is not ("admin" or "maintain" or "write"))
            throw new InvalidDataException("A verified human command and explicit workflow generations are required.");
        EventData data = notification.Kind switch
        {
            // Live readiness can defer work, but cannot rewrite an immutable source notification.
            NotificationKind.Readiness when IsIsolatedStatusTestStart(payload) =>
                new Ready(SinglePassTest: true),
            NotificationKind.Readiness when IsPocStart(payload) =>
                new Ready(maxRepairTasks),
            NotificationKind.Readiness or NotificationKind.WithdrawReadiness =>
                payload.GetProperty("action").GetString() == "labeled" ? new Ready(maxRepairTasks) : new Withdraw(),
            NotificationKind.Projection => new StageEdited(),
            NotificationKind.Closed => new Closure(),
            NotificationKind.Reopened => new Reopen(),
            NotificationKind.Resume => new Resume(),
            NotificationKind.Pause => new Pause(notification.Reason!),
            NotificationKind.Reactivate => new Reactivate(notification.Reason!),
            NotificationKind.ResolveTask => new ResolveTaskTerminal(payload.GetProperty("inputs").GetProperty("task").GetString()!,
                "", notification.Reason!),
            NotificationKind.ClassifyReview => ReadClassification(payload.GetProperty("inputs"), notification.Reason!),
            NotificationKind.RequestChanges => ReadChanges(payload.GetProperty("inputs"), notification.Reason!),
            NotificationKind.AbandonUncertainReview => ReadReviewAbandonment(payload.GetProperty("inputs"), notification.Reason!),
            NotificationKind.AbandonUncertainTaskLaunch => ReadTaskLaunchAbandonment(payload.GetProperty("inputs"), notification.Reason!),
            _ => throw new InvalidDataException("Unsupported command.")
        };
        return new(notification.SourceId, evidence.Repository, notification.IssueNumber,
            repositoryGeneration, issueGeneration,
            new Actor(notification.ActorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ActorKind.Human, notification.ActorId,
                Type: notification.Kind is NotificationKind.ResolveTask or NotificationKind.ClassifyReview or
                    NotificationKind.RequestChanges or NotificationKind.AbandonUncertainReview or
                    NotificationKind.AbandonUncertainTaskLaunch ? evidence.ActorType : null),
            occurredAt, data);
    }

    public static ActionsNotification Parse(
        JsonElement payload, string eventName, string runId, IngressEvidence evidence)
    {
        if (evidence is null || evidence.ProjectionActorIds is null ||
            evidence.RepositoryId <= 0 || evidence.IssueId <= 0 || evidence.IssueNumber <= 0)
            throw new InvalidDataException("Verified repository and Issue identities are required.");
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
            if (operation is "status-test-start" or "poc-start" && (!evidence.IssueOpen || !evidence.Ready))
                throw new InvalidDataException("Isolated test start requires an open Issue with explicit readiness.");
            var kind = operation switch
            {
                "resume" => NotificationKind.Resume,
                "pause" => NotificationKind.Pause,
                "status-test-start" => NotificationKind.Readiness,
                "status-test-bootstrap" => NotificationKind.Ignore,
                "poc-start" => NotificationKind.Readiness,
                "reactivate" => NotificationKind.Reactivate,
                "resolve-task" => NotificationKind.ResolveTask,
                "classify-review" => NotificationKind.ClassifyReview,
                "request-changes" => NotificationKind.RequestChanges,
                "abandon-uncertain-review" => NotificationKind.AbandonUncertainReview,
                "poc-abandon-uncertain-launch" => NotificationKind.AbandonUncertainTaskLaunch,
                _ => throw new InvalidDataException("Only guarded recovery operations are supported.")
            };
            var reason = inputs.TryGetProperty("reason", out var value) ? value.GetString() : null;
            if ((kind is NotificationKind.Pause or NotificationKind.Reactivate or NotificationKind.ResolveTask or
                NotificationKind.ClassifyReview or NotificationKind.RequestChanges or NotificationKind.AbandonUncertainReview or
                NotificationKind.AbandonUncertainTaskLaunch) &&
                string.IsNullOrWhiteSpace(reason))
                throw new InvalidDataException("Pause/reactivation requires a reason.");
            if (kind == NotificationKind.ResolveTask &&
                (!inputs.TryGetProperty("task", out var task) || string.IsNullOrWhiteSpace(task.GetString()) || task.GetString()!.Length > 256))
                throw new InvalidDataException("Terminal task resolution requires the canonical task identity.");
            if (kind == NotificationKind.AbandonUncertainReview &&
                (!inputs.TryGetProperty("review_intent", out var intent) ||
                 string.IsNullOrWhiteSpace(intent.GetString()) || intent.GetString()!.Length > 256 ||
                 !inputs.TryGetProperty("pull", out var pull) || !int.TryParse(pull.GetString(), out var pullNumber) ||
                 pullNumber <= 0 ||
                 !inputs.TryGetProperty("sha", out var sha) ||
                 sha.GetString() is not { Length: 40 } reviewedSha || !reviewedSha.All(Uri.IsHexDigit) ||
                 !inputs.TryGetProperty("acknowledge_possible_prior_submission", out var acknowledge) ||
                 !IsExplicitTrue(acknowledge)))
                throw new InvalidDataException("Review abandonment requires its exact intent and explicit ambiguity acknowledgement.");
            if (kind == NotificationKind.AbandonUncertainTaskLaunch &&
                (!inputs.TryGetProperty("task", out var taskId) || string.IsNullOrWhiteSpace(taskId.GetString()) ||
                 taskId.GetString()!.Length > 256 ||
                 !inputs.TryGetProperty("launch_intent", out var launchIntent) ||
                 string.IsNullOrWhiteSpace(launchIntent.GetString()) || launchIntent.GetString()!.Length > 256 ||
                 !inputs.TryGetProperty("acknowledge_no_task_created", out var acknowledged) ||
                 acknowledged.GetString() != "POC-249-ACK-NO-TASK-CREATED"))
                throw new InvalidDataException("Task-launch abandonment requires its exact identity and explicit operator attestation.");
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

    private static RequestChanges ReadChanges(JsonElement inputs, string reason) =>
        new(inputs.GetProperty("handoff").GetString() ?? throw new InvalidDataException("Missing handoff."),
            int.Parse(inputs.GetProperty("round").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(inputs.GetProperty("pull").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            inputs.GetProperty("sha").GetString() ?? throw new InvalidDataException("Missing head SHA."),
            reason);

    private static AbandonUncertainReview ReadReviewAbandonment(JsonElement inputs, string reason) =>
        new(inputs.GetProperty("review_intent").GetString()!,
            int.Parse(inputs.GetProperty("pull").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            inputs.GetProperty("sha").GetString() ?? throw new InvalidDataException("Missing head SHA."),
            reason, IsExplicitTrue(inputs.GetProperty("acknowledge_possible_prior_submission")));

    private static AbandonUncertainTaskLaunch ReadTaskLaunchAbandonment(JsonElement inputs, string reason) =>
        new(inputs.GetProperty("task").GetString()!, inputs.GetProperty("launch_intent").GetString()!,
            reason, inputs.GetProperty("acknowledge_no_task_created").GetString() ==
                "POC-249-ACK-NO-TASK-CREATED");

    private static bool IsExplicitTrue(JsonElement value) =>
        value.ValueKind == JsonValueKind.True ||
        value.ValueKind == JsonValueKind.String && value.GetString() == "true";

    private static HumanClassification ReadClassification(JsonElement inputs, string reason)
    {
        var classification = inputs.GetProperty("classification").GetString() switch
        {
            "accepted" => ReviewClassification.Accepted,
            "blocking" => ReviewClassification.Blocking,
            "needs-human" => ReviewClassification.NeedsHuman,
            _ => throw new InvalidDataException("Explicit review classification is required.")
        };
        string Text(string name) => inputs.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing classification input.");
        List<T> Items<T>(string name)
        {
            var text = inputs.TryGetProperty(name, out var value) ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) return [];
            if (text.Length > 16 * 1024) throw new InvalidDataException("Classification evidence exceeds capacity.");
            return JsonSerializer.Deserialize<List<T>>(text, StateJson.Options) ??
                throw new InvalidDataException("Invalid classification evidence.");
        }
        return new(Text("handoff"), int.Parse(Text("round"), System.Globalization.CultureInfo.InvariantCulture),
            int.Parse(Text("pull"), System.Globalization.CultureInfo.InvariantCulture), Text("sha"), classification,
            reason, Items<Finding>("findings"), Items<FindingResponse>("responses"));
    }

    private static bool IsIsolatedStatusTestStart(JsonElement payload) =>
        payload.TryGetProperty("inputs", out var inputs) &&
        inputs.TryGetProperty("operation", out var operation) &&
        operation.GetString() == "status-test-start";

    private static bool IsPocStart(JsonElement payload) =>
        payload.TryGetProperty("inputs", out var inputs) &&
        inputs.TryGetProperty("operation", out var operation) &&
        operation.GetString() == "poc-start";
}
