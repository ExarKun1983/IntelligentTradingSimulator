using System.Net;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubTaskLaunchDiagnostic(
    DateTimeOffset StartedAt,
    string Outcome,
    int? HttpStatus,
    string? GitHubRequestId,
    string? ErrorCategory,
    string? ErrorType,
    string? ResponseKind,
    bool? HasTaskId,
    bool? HasRepository,
    bool? HasCreator,
    bool? HasState);

internal sealed record GitHubApiResponseMetadata(int StatusCode, string? RequestId);

public sealed class GitHubTaskLaunchDiagnostics
{
    private readonly object gate = new();
    private GitHubTaskLaunchDiagnostic? current;

    public GitHubTaskLaunchDiagnostic? Snapshot
    {
        get { lock (gate) return current; }
    }

    internal void Begin()
    {
        lock (gate)
            current = new(DateTimeOffset.UtcNow, "request_started", null, null, null, null, null,
                null, null, null, null);
    }

    internal void ObserveResponse(GitHubApiResponseMetadata response)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                Outcome = "response_received",
                HttpStatus = response.StatusCode,
                GitHubRequestId = response.RequestId
            };
        }
    }

    internal void ObserveBody(JsonElement body)
    {
        lock (gate)
        {
            if (current is null) return;
            current = current with
            {
                ResponseKind = body.ValueKind.ToString(),
                HasTaskId = HasNonEmptyString(body, "id"),
                HasRepository = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("repository", out _),
                HasCreator = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("creator", out _),
                HasState = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("state", out _)
            };
        }
    }

    internal void MarkValidated()
    {
        lock (gate)
        {
            if (current is not null) current = current with { Outcome = "task_identity_validated" };
        }
    }

    internal void RecordFailure(Exception exception)
    {
        lock (gate)
        {
            if (current is null) return;
            var category = exception switch
            {
                HttpRequestException when current.HttpStatus is >= 400 => "http_status",
                HttpRequestException => "transport",
                JsonException => "invalid_json",
                InvalidDataException or InvalidOperationException or KeyNotFoundException => "response_validation",
                OperationCanceledException => "cancelled",
                _ => "other"
            };
            current = current with
            {
                Outcome = "failed",
                ErrorCategory = category,
                ErrorType = exception.GetType().Name
            };
        }
    }

    private static bool HasNonEmptyString(JsonElement body, string property) =>
        body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString());
}
