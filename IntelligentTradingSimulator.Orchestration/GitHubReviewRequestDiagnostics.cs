using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubReviewRequestDiagnostic(
    string IntentId, int Issue, DateTimeOffset StartedAt, string Stage, string Outcome,
    bool PostAttempted = false, int? HttpStatus = null, string? GitHubRequestId = null,
    int? ReturnedReviewerCount = null, bool? HasVerifiedReviewer = null,
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

    internal void ObserveResponse(GitHubApiResponseMetadata response)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { HttpStatus = response.StatusCode, GitHubRequestId = response.RequestId };
            Write();
        }
    }

    internal void ObserveReviewers(int count, bool verified)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { ReturnedReviewerCount = count, HasVerifiedReviewer = verified };
            Write();
        }
    }

    internal void Complete()
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with { Stage = "acknowledgement_saved", Outcome = "confirmed" };
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
                HttpRequestException => "transport",
                JsonException => "invalid_json",
                InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException => "validation",
                OperationCanceledException => "cancelled",
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
