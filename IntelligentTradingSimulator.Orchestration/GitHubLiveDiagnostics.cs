using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public enum GitHubLiveStage
{
    Invocation, Settings, Credentials, StateLoad, CommandVerification, WakeVerification,
    CommandPublication, WorkerLease, InFlightRecovery, WorkerStateLoad, WorkerEventRead, TaskMapping,
    TaskCompletion, Feedback, CiObservation, CiCandidateValidation, CandidateRepository,
    CandidateTask, CandidateSessions, CandidateMapping, CiLiveEligibility, CiRunIdentity,
    CiJobEvidence, ReviewObservation, EventPublication, LiveObservation, ReconcileDecision,
    ReconcileStateSave, EffectRecovery, EffectSelection, EffectExecution, EffectOutcomeSave,
    WorkerLeaseRelease
}

public sealed class GitHubLiveDiagnostics
{
    private GitHubLiveStage stage = GitHubLiveStage.Invocation;
    private (GitHubLiveStage Stage, string ErrorType, int? HttpStatus)? failure;

    internal GitHubLiveStage Stage => stage;
    internal void Mark(GitHubLiveStage value) => stage = value;

    internal void Capture(Exception exception)
    {
        failure ??= (stage, exception switch
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
        }, exception is HttpRequestException { StatusCode: { } status } ? (int)status : null);
    }

    internal void WriteFailure(TextWriter error, Exception exception)
    {
        Capture(exception);
        var saved = failure!.Value;
        error.WriteLine(JsonSerializer.Serialize(new
        {
            LiveDiagnostic = new { Stage = saved.Stage.ToString(), saved.ErrorType, saved.HttpStatus }
        }));
    }
}
