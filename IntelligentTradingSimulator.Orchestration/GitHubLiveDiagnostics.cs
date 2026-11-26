using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelligentTradingSimulator.Orchestration;

public enum GitHubLiveStage
{
    Invocation, Settings, Credentials, StateLoad, CommandVerification, WakeVerification,
    CommandPublication, WorkerLease, InFlightRecovery, WorkerStateLoad, WorkerEventRead, TaskMapping,
    TaskCompletion, Feedback, CiObservation, CiCandidateValidation, CandidateRepository,
    CandidateTask, CandidateSessions, CandidateMapping, CiLiveEligibility, CiRunIdentity,
    CiJobEvidence, ReviewObservation, EventPublication, LiveObservation, ReconcileDecision,
    ReconcileStateSave, EffectRecovery, EffectSelection, EffectExecution, EffectOutcomeSave,
    WorkerLeaseRelease, RequiredStatusPreflight, RequiredStatusLookup, RequiredStatusBoundary,
    RequiredStatusPublication, RequiredStatusConfirmation, RequiredStatusEligibility,
    RequiredStatusPublisher, RequiredStatusRepository, RequiredStatusLiveHead,
    RequiredStatusApproval, RequiredStatusInitialStateLoad, RequiredStatusPreparedStateLoad,
    RequiredStatusConfirmationStateLoad, RequiredStatusRecoveryStateLoad,
    RequiredStatusCanonicalIntent, RequiredStatusPreparedCheck
}

public enum GitStateReadStep { Ref, Commit, Tree, Blob, CommitIdentity, Deserialize, RepositorySchema, Continuation }
public enum GitStateReadCheck
{
    RefExists, RefIdentity, RequestedCommitIdentity, TreeIdentity, TruncatedTree,
    StateTreeEntry, BlobIdentity, BlobEncoding, BlobSize, CommitIdentity, Repository, Schema
}
public enum RequiredStatusIntentCheck
{
    Repository, IntentKind, IntentExists, Digest, DeliveryState, SavedKind, IssueNumber,
    Generation, Task, Round, ShaMatches, Payload, Context, ShaFormat, IssueExists, Unprepared
}

public sealed class GitHubLiveDiagnostics(TextWriter? effectError = null)
{
    private const string RequiredStatusEvidenceKey = "ITS.RequiredStatusValidation";
    private const string StateReadCheckKey = "ITS.GitStateReadCheck";
    private GitHubLiveStage stage = GitHubLiveStage.Invocation;
    private (GitHubLiveStage Stage, string ErrorType, int? HttpStatus, GitStateReadStep? StateRead,
        RequiredStatusValidation? Validation, GitStateReadCheck? StateReadCheck)? failure;
    private bool leaseReleaseAlsoFailed;
    private GitStateReadStep? stateRead;
    private long? expectedExecutingVersion;

    internal GitHubLiveStage Stage => stage;
    internal void Mark(GitHubLiveStage value)
    {
        stage = value;
        stateRead = null;
    }
    internal void MarkStateRead(GitStateReadStep value) => stateRead = value;
    internal void BeginEffectExecution(EffectKind kind, long version) =>
        expectedExecutingVersion = kind == EffectKind.PublishRequiredStatus ? version : null;
    internal void EndEffectExecution() => expectedExecutingVersion = null;
    internal void AttachStateReadCheck(Exception exception, GitStateReadCheck check) =>
        exception.Data[StateReadCheckKey] = check;
    internal void MarkLeaseReleaseFailure() => leaseReleaseAlsoFailed = true;

    internal void AttachRequiredStatusValidation(Exception exception, RequiredStatusIntentCheck check,
        RepositoryState state, EffectIntent? saved, EffectIntent supplied)
    {
        exception.Data[RequiredStatusEvidenceKey] = new RequiredStatusValidation(
            check.ToString(), expectedExecutingVersion, state.Version,
            saved is null ? null : Enum.IsDefined(saved.Status) ? saved.Status.ToString() : "Invalid",
            supplied.RequiredStatusBoundary is not null || saved?.RequiredStatusBoundary is not null);
    }

    internal void Capture(Exception exception)
    {
        failure ??= (stage, ErrorType(exception), HttpStatus(exception), stateRead,
            exception.Data[RequiredStatusEvidenceKey] as RequiredStatusValidation,
            exception.Data[StateReadCheckKey] as GitStateReadCheck?);
    }

    internal void ReportEffectFailure(EffectKind kind, EffectFailureCode code, Exception exception)
    {
        effectError?.WriteLine(JsonSerializer.Serialize(new
        {
            EffectDiagnostic = new EffectDiagnostic(
                stage.ToString(), kind.ToString(), code.ToString(), ErrorType(exception), HttpStatus(exception))
            {
                StateRead = stateRead?.ToString(),
                StateReadCheck = (exception.Data[StateReadCheckKey] as GitStateReadCheck?)?.ToString(),
                RequiredStatusValidation = exception.Data[RequiredStatusEvidenceKey] as RequiredStatusValidation
            }
        }));
    }

    private static string ErrorType(Exception exception) => exception switch
    {
        InvalidDataException => "InvalidDataException",
        HttpRequestException => "HttpRequestException",
        JsonException => "JsonException",
        InvalidOperationException => "InvalidOperationException",
        UnauthorizedAccessException => "UnauthorizedAccessException",
        KeyNotFoundException => "KeyNotFoundException",
        FormatException => "FormatException",
        OverflowException => "OverflowException",
        ArgumentException => "ArgumentException",
        OperationCanceledException => "OperationCanceledException",
        IOException => "IOException",
        _ => "Other"
    };

    private static int? HttpStatus(Exception exception) =>
        exception is HttpRequestException { StatusCode: { } status } ? (int)status : null;

    internal void WriteFailure(TextWriter error, Exception exception)
    {
        Capture(exception);
        var saved = failure!.Value;
        error.WriteLine(JsonSerializer.Serialize(new
        {
            LiveDiagnostic = new LiveDiagnostic(saved.Stage.ToString(), saved.ErrorType, saved.HttpStatus)
            {
                StateRead = saved.StateRead?.ToString(),
                StateReadCheck = saved.StateReadCheck?.ToString(),
                RequiredStatusValidation = saved.Validation
            }
        }));
        if (leaseReleaseAlsoFailed)
            error.WriteLine("Worker lease release also failed; the lease may remain held.");
    }

    private sealed record RequiredStatusValidation(string Check, long? ExpectedExecutingVersion,
        long ObservedVersion, string? SavedDeliveryState, bool BoundaryPresent);
    private sealed record EffectDiagnostic(string Stage, string Effect, string FailureCode, string ErrorType, int? HttpStatus)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StateRead { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StateReadCheck { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RequiredStatusValidation? RequiredStatusValidation { get; init; }
    }
    private sealed record LiveDiagnostic(string Stage, string ErrorType, int? HttpStatus)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StateRead { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? StateReadCheck { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RequiredStatusValidation? RequiredStatusValidation { get; init; }
    }
}
