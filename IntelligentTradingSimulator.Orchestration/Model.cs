using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelligentTradingSimulator.Orchestration;

public enum Phase { READY, CODING, CI_PENDING, REVIEW_PENDING, FIX_PENDING, HUMAN_REVIEW, BLOCKED, CLOSED }
public enum TaskKind { Coding, CiRepair, ReviewRepair, FeedbackRepair }
public enum TaskLaunchSource { PullRequestHead, UnmappedIssue }
public enum TaskStatus { Intended, Executing, Running, AwaitingHeadConfirmation, Completed, Failed, Unknown }
public enum EffectKind { LaunchTask, RunCi, RequestReview, ProjectLabels, ProjectStatus, PublishRequiredStatus }
public enum DeliveryStatus { Pending, Executing, Succeeded, Failed, Unknown, Cancelled }
public enum ReviewClassification { Accepted, Blocking, NeedsHuman }
public enum ActorKind { Human, TaskService, CiService, ReviewService }
public enum TerminalOutcome { None, Merged, Unmerged, Unknown }

public sealed record Actor(string Login, ActorKind Kind, long? Id = null, string? NodeId = null, string? Type = null);

public sealed class RepositoryState
{
    public static RepositoryState Create(string repository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        return new RepositoryState { Repository = repository };
    }

    public int SchemaVersion { get; set; } = 1;
    public long Generation { get; set; } = 1;
    public long Version { get; set; }
    public string Repository { get; set; } = "";
    public string? LastEventId { get; set; }
    public int? AdmittedIssue { get; set; }
    public WorkerLease? WorkerLease { get; set; }
    public ContinuationTicket? Continuation { get; set; }
    public Dictionary<int, IssueState> Issues { get; set; } = [];
    public Dictionary<string, InboxEntry> Inbox { get; set; } = [];
    public Dictionary<string, EffectIntent> Outbox { get; set; } = [];
}

public sealed record WorkerLease(string Owner, DateTimeOffset ExpiresAt);

public sealed class IssueState
{
    public int SchemaVersion { get; set; } = 1;
    public int Number { get; set; }
    public long Generation { get; set; } = 1;
    public long Version { get; set; }
    public string? LastEventId { get; set; }
    public Phase Phase { get; set; } = Phase.READY;
    public bool Ready { get; set; }
    public bool ActiveAuthorized { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? FeedbackSince { get; set; }
    public bool ReactivationRequired { get; set; }
    public bool Paused { get; set; }
    public string? HoldReason { get; set; }
    public int? PullRequest { get; set; }
    public string? HeadSha { get; set; }
    public int Round { get; set; }
    public int ConsecutiveCiFailures { get; set; }
    public int ConsecutiveReviewFailures { get; set; }
    public bool PendingReviewProgress { get; set; }
    public int CiBudget { get; set; } = 3;
    public int ReviewBudget { get; set; } = 5;
    public int? MaxRepairTasks { get; set; }
    public string? ActiveTaskId { get; set; }
    public Dictionary<string, TaskIdentity> Tasks { get; set; } = [];
    public CiIdentity? Ci { get; set; }
    public ReviewIdentity? Review { get; set; }
    public List<string> FeedbackEventIds { get; set; } = [];
    public string? LastFeedbackText { get; set; }
    public bool ClosurePending { get; set; }
    public TerminalOutcome TerminalOutcome { get; set; }
    public List<TerminalRecord> TerminalHistory { get; set; } = [];
    public string? CurrentHandoffId { get; set; }
    public Dictionary<string, HumanHandoff> Handoffs { get; set; } = [];
    public Dictionary<string, FeedbackRecord> Feedback { get; set; } = [];
    public List<string> PendingFeedbackEventIds { get; set; } = [];
    public Dictionary<string, ReactivationRecord> Reactivations { get; set; } = [];
    public Dictionary<string, HumanClassificationRecord> Classifications { get; set; } = [];
    public string? ProjectionDigest { get; set; }
    public string? ObservedProjectionDigest { get; set; }
    public string? StatusProjectionDigest { get; set; }
    public long RequiredStatusSequence { get; set; }
    public string? RequiredStatusIntentId { get; set; }
}

public sealed class TaskIdentity
{
    public string Id { get; set; } = "";
    public TaskKind Kind { get; set; }
    public TaskStatus Status { get; set; }
    public string? ParentTaskId { get; set; }
    public int Round { get; set; }
    public TaskLaunchSource LaunchSource { get; set; }
    public string? SourceSha { get; set; }
    public string? FinalSha { get; set; }
    public bool LaunchConfirmed { get; set; }
    public string? ExternalId { get; set; }
    public TaskTerminalRecord? TerminalEvidence { get; set; }
    public TaskOperatorResolution? OperatorResolution { get; set; }
    public CandidateShaEvidence? CandidateShaEvidence { get; set; }
    public List<Finding> ReviewBlockers { get; set; } = [];
    public List<Finding> CiBlockers { get; set; } = [];
    public List<string> FeedbackEventIds { get; set; } = [];
}

public sealed class CiIdentity
{
    public string IntentId { get; set; } = "";
    public string? RunId { get; set; }
    public string TaskId { get; set; } = "";
    public int Round { get; set; }
    public string Sha { get; set; } = "";
    public bool? Green { get; set; }
    public string? TerminalStopReason { get; set; }
    public List<Finding> Blockers { get; set; } = [];
    public CiDispatchBoundary? DispatchBoundary { get; set; }
}

public sealed record CiDispatchBoundary(long WorkflowId, string WorkflowSha, string Ref, string Correlation,
    DateTimeOffset PreparedAt, long? RunId = null);

public sealed class ReviewIdentity
{
    public string IntentId { get; set; } = "";
    public string? ReviewId { get; set; }
    public string TaskId { get; set; } = "";
    public int Round { get; set; }
    public string Sha { get; set; } = "";
    public ReviewClassification? Classification { get; set; }
    public List<Finding> Findings { get; set; } = [];
    public List<FindingResponse> Responses { get; set; } = [];
    public ReviewRequestBoundary? RequestBoundary { get; set; }
    public string? SourceEvidence { get; set; }
}

public sealed record ReviewRequestBoundary(long ReviewerId, string ReviewerNodeId, DateTimeOffset PreparedAt,
    List<long> ExistingReviewIds, bool Confirmed = false);

public sealed record Finding(string Id, string Description, string Evidence);
public sealed record FindingResponse(string FindingId, string Evidence, bool Resolved);
public sealed record InboxEntry(string Id, string Digest, DeliveryStatus Status, DateTimeOffset ReceivedAt,
    DateTimeOffset CompletedAt, string? PayloadJson = null)
{
    [JsonIgnore]
    public OrchestrationEvent? Event => PayloadJson is null ? null : StateJson.DeserializeEvent(PayloadJson);
}
public sealed record HumanHandoff(string Id, long Generation, string? ReviewId, string? TaskId,
    int Round, string? Sha, string Reason, DateTimeOffset CreatedAt, string? ClassificationEventId = null);
public sealed record FeedbackRecord(string EventId, string Text, Actor Author, DateTimeOffset OccurredAt,
    long Generation, string? HandoffId, int Round, string? Sha, string? PlannedTaskId = null,
    string? HandedOffTaskId = null, DateTimeOffset? HandedOffAt = null);
public sealed record ReactivationRecord(string EventId, long PreviousGeneration, long Generation,
    string Reason, Actor Author, DateTimeOffset OccurredAt);
public sealed record HumanClassificationRecord(string EventId, string HandoffId, Actor Author, string Reason,
    int Round, string Sha, ReviewClassification Classification, List<Finding> Findings,
    List<FindingResponse> Responses, DateTimeOffset OccurredAt);
public sealed record TerminalRecord(long Generation, TerminalOutcome Outcome, int? PullRequest,
    string? Sha, DateTimeOffset ObservedAt);
public sealed record TaskSessionTerminalEvidence(string Id, string ExternalTaskId, long RepositoryId,
    string State, DateTimeOffset CompletedAt);
public sealed record TaskTerminalEvidence(string LaunchIntentId, string ExternalTaskId, string Repository,
    long RepositoryId, long CreatorId, string State, int SessionCount,
    List<TaskSessionTerminalEvidence> Sessions, DateTimeOffset UpdatedAt, DateTimeOffset ObservedAt,
    string ResponseDigest);
public sealed record TaskTerminalRecord(string EventId, TaskTerminalEvidence Evidence);
public sealed record CandidateShaEvidence(int Version, string Policy, string TaskId, string ExternalTaskId,
    List<string> SessionIds, int PullRequest, string ObservedHeadSha);
public sealed record TaskOperatorResolution(string EventId, string EvidenceEventId, Actor Author,
    string Reason, DateTimeOffset OccurredAt);

public sealed class EffectIntent
{
    public string Id { get; set; } = "";
    public string Digest { get; set; } = "";
    public int IssueNumber { get; set; }
    public long Generation { get; set; }
    public EffectKind Kind { get; set; }
    public DeliveryStatus Status { get; set; }
    public string? TaskId { get; set; }
    public int Round { get; set; }
    public string? Sha { get; set; }
    public string? ExternalId { get; set; }
    public List<string> Labels { get; set; } = [];
    public List<string> FeedbackEventIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? StatusBody { get; set; }
    public string? TaskPrompt { get; set; }
    public RequiredStatusPayload? RequiredStatus { get; set; }
    public RequiredStatusBoundary? RequiredStatusBoundary { get; set; }
}

public sealed record RequiredStatusPayload(string Context, string State, string Description, string TargetUrl,
    string? CiIntentId, string? CiRunId);
public sealed record RequiredStatusBoundary(long ActorId, string ActorLogin, string ActorNodeId, string ActorType,
    DateTimeOffset PreparedAt, List<long> ExistingStatusIds, long? StatusId = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(Withdraw), "withdraw")]
[JsonDerivedType(typeof(StageEdited), "stage-edited")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(Reactivate), "reactivate")]
[JsonDerivedType(typeof(TaskCompleted), "task-completed")]
[JsonDerivedType(typeof(TaskPrMapped), "task-pr-mapped")]
[JsonDerivedType(typeof(TaskStopped), "task-stopped")]
[JsonDerivedType(typeof(TaskTerminalObserved), "task-terminal-observed")]
[JsonDerivedType(typeof(ResolveTaskTerminal), "resolve-task-terminal")]
[JsonDerivedType(typeof(HeadChanged), "head-changed")]
[JsonDerivedType(typeof(CiResult), "ci-result")]
[JsonDerivedType(typeof(CiStopped), "ci-stopped")]
[JsonDerivedType(typeof(ReviewResult), "review-result")]
[JsonDerivedType(typeof(HumanFeedback), "human-feedback")]
[JsonDerivedType(typeof(HumanClassification), "human-classification")]
[JsonDerivedType(typeof(Closure), "closure")]
[JsonDerivedType(typeof(Reopen), "reopen")]
public abstract record EventData;
public sealed record Ready(int? MaxRepairTasks = null) : EventData;
public sealed record Withdraw : EventData;
public sealed record StageEdited : EventData;
public sealed record Resume : EventData;
public sealed record Pause(string Reason) : EventData;
public sealed record Reactivate(string Reason) : EventData;
public sealed record TaskCompleted(string TaskId, int PullRequest, string Sha,
    CandidateShaEvidence? Evidence = null) : EventData;
public sealed record TaskPrMapped(string TaskId, int PullRequest, string ObservedHead) : EventData;
public sealed record TaskStopped(string TaskId, string Reason) : EventData;
public sealed record TaskTerminalObserved(string TaskId, TaskTerminalEvidence Evidence) : EventData;
public sealed record ResolveTaskTerminal(string TaskId, string EvidenceEventId, string Reason) : EventData;
public sealed record HeadChanged(int PullRequest, string Sha) : EventData;
public sealed record CiResult(string RunId, string TaskId, int Round, int PullRequest, string Sha, bool Green,
    List<Finding>? Blockers = null) : EventData;
public sealed record CiStopped(string RunId, string TaskId, int Round, string Sha, string Reason) : EventData;
public sealed record ReviewResult(string ReviewId, string TaskId, int Round, int PullRequest, string Sha,
    ReviewClassification Classification, List<Finding> Findings, List<FindingResponse> Responses,
    string? SourceEvidence = null) : EventData;
public sealed record HumanFeedback(string Text, string? HandoffId = null, int? Round = null, string? Sha = null) : EventData;
public sealed record HumanClassification(string HandoffId, int Round, int PullRequest, string Sha,
    ReviewClassification Classification, string Reason, List<Finding>? Findings = null,
    List<FindingResponse>? Responses = null) : EventData;
public sealed record Closure : EventData;
public sealed record Reopen : EventData;

public sealed record OrchestrationEvent(string Id, string Repository, int IssueNumber, long RepositoryGeneration,
    long IssueGeneration, Actor Actor, DateTimeOffset OccurredAt, EventData Data)
{
    public string Digest => StateJson.Digest(JsonSerializer.Serialize(this, StateJson.EventOptions));
}

public interface IAuthorizationPolicy
{
    // Implementations must verify the authenticated upstream identity, repository and issue scope.
    bool IsAuthorized(OrchestrationEvent message);
}

public sealed class DenyAllAuthorizationPolicy : IAuthorizationPolicy
{
    public bool IsAuthorized(OrchestrationEvent message) => false;
}

public sealed class LiveObservation
{
    public DateTimeOffset ObservedAt { get; set; }
    public Dictionary<int, LiveIssueObservation> Issues { get; set; } = [];
}

public sealed class LiveIssueObservation
{
    public bool Ready { get; set; }
    public bool Closed { get; set; }
    public int? PullRequest { get; set; }
    public string? HeadSha { get; set; }
    public string? CompletedTaskId { get; set; }
    public bool? PullRequestMerged { get; set; }
    public List<string> Labels { get; set; } = [];
}

public sealed record ReconcileDecision(RepositoryState State, IReadOnlyList<EffectIntent> PendingEffects);

public static class StateJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    // Do not serialize the computed Digest property back into its own input.
    internal static JsonSerializerOptions EventOptions { get; } = CreateEventOptions();
    private static JsonSerializerOptions CreateEventOptions()
    {
        var resolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(OrchestrationEvent))
                foreach (var property in info.Properties.Where(p => p.Name == nameof(OrchestrationEvent.Digest)).ToList())
                    info.Properties.Remove(property);
        });
        return new JsonSerializerOptions { TypeInfoResolver = resolver };
    }
    public static string Serialize(RepositoryState state) => JsonSerializer.Serialize(state, Options);
    public static RepositoryState Deserialize(string json) =>
        JsonSerializer.Deserialize<RepositoryState>(json, Options) ?? throw new InvalidDataException("Missing state.");
    internal static RepositoryState Clone(RepositoryState state) => Deserialize(Serialize(state));
    internal static string SerializeEvent(OrchestrationEvent message) => JsonSerializer.Serialize(message, EventOptions);
    internal static OrchestrationEvent DeserializeEvent(string json) =>
        JsonSerializer.Deserialize<OrchestrationEvent>(json, EventOptions) ?? throw new InvalidDataException("Missing event payload.");
    internal static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
