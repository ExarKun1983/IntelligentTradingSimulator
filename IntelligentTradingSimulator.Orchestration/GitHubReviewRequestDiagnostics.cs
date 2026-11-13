using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubReviewRequestDiagnostic(
    string IntentId, int Issue, DateTimeOffset StartedAt, string Stage, string Outcome,
    bool PostAttempted = false, int? HttpStatus = null, string? GitHubRequestId = null,
    string? RequestNodeId = null, string? ResponseState = null, string? FailureReason = null,
    int Attempt = 0, int? RetryAfterSeconds = null,
    string? ErrorCategory = null, string? ErrorType = null);

public sealed class GitHubReviewRequestDiagnostics(TextWriter? log = null)
{
    private readonly object gate = new();
    private GitHubReviewRequestDiagnostic? current;

    public GitHubReviewRequestDiagnostic? Snapshot
    {
        get { lock (gate) return current; }
    }

    internal void Begin(EffectIntent intent)
    {
        lock (gate)
        {
            current = new(intent.Id, intent.IssueNumber, DateTimeOffset.UtcNow, "approval", "in_progress");
            Write();
        }
    }

    internal void Stage(string stage)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { Stage = stage, PostAttempted = current.PostAttempted || stage == "post" };
            Write();
        }
    }

    internal void BeginAttempt(int attempt)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                Stage = "post", PostAttempted = true, Attempt = attempt,
                HttpStatus = null, GitHubRequestId = null, RequestNodeId = null,
                ResponseState = null, FailureReason = null, RetryAfterSeconds = null,
                ErrorCategory = null, ErrorType = null
            };
            Write();
        }
    }

    internal void ObserveResponse(GitHubCopilotReviewResponse response, int attempt)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                HttpStatus = response.StatusCode, GitHubRequestId = response.RequestId,
                RequestNodeId = response.NodeId, ResponseState = response.State,
                FailureReason = response.FailureReason, Attempt = attempt,
                RetryAfterSeconds = response.RetryAfter is { } retry ? (int)Math.Clamp(retry.TotalSeconds, 0, 3600) : null
            };
            Write();
        }
    }

    internal void ObserveResponseMetadata(GitHubApiResponseMetadata metadata, int attempt)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                HttpStatus = metadata.StatusCode,
                GitHubRequestId = metadata.RequestId,
                Attempt = attempt,
                RetryAfterSeconds = metadata.RetryAfter is { } retry
                    ? (int)Math.Clamp(retry.TotalSeconds, 0, 3600) : null
            };
            Write();
        }
    }

    internal void Complete(string outcome = "confirmed")
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { Stage = "acknowledgement_saved", Outcome = outcome };
            Write();
        }
    }

    internal void RecordApiFailure(string failureReason)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                Outcome = "rejected", Stage = "api_rejected",
                FailureReason = failureReason, ErrorCategory = "http_status"
            };
            Write();
        }
    }

    internal void RecordFailure(Exception exception)
    {
        lock (gate)
        {
            if (current is null) return;
            var status = exception is HttpRequestException { StatusCode: { } code } ? (int)code : current.HttpStatus;
            var category = exception switch
            {
                HttpRequestException when status is < 200 or >= 300 => "http_status",
                HttpRequestException when status == 201 => "response_body",
                HttpRequestException => "transport",
                JsonException => "invalid_json",
                InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException => "validation",
                OperationCanceledException => "cancelled",
                IOException when current.Stage == "acknowledgement_save" => "persistence_or_io",
                IOException when current.HttpStatus is < 200 or >= 300 => "http_status",
                IOException when current.HttpStatus == 201 => "response_body",
                IOException when current.Stage == "post" => "transport",
                IOException => "persistence_or_io",
                _ => "other"
            };
            current = current with
            {
                Outcome = "failed", HttpStatus = status, ErrorCategory = category,
                ErrorType = exception.GetType().Name
            };
            Write();
        }
    }

    internal void Refuse(string category)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { Outcome = "refused", ErrorCategory = category };
            Write();
        }
    }

    private void Write()
    {
        // Only controlled stages, exception types and allowlisted response metadata; never response bodies/messages.
        log?.WriteLine(JsonSerializer.Serialize(new { ReviewRequestDiagnostic = current }, StateJson.Options));
        log?.Flush();
    }
}
