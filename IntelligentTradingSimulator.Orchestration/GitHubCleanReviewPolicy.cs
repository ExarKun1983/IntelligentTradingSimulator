using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

internal static class GitHubCleanReviewPolicy
{
    public const string Name = "exact-head-clean-review-v1";

    public static bool Matches(ReviewRequestBoundary boundary, IReadOnlyList<JsonElement> submissions,
        IReadOnlyList<JsonElement> comments, DateTimeOffset classifiedAt)
    {
        if (!boundary.Confirmed || boundary.AlreadyPending || string.IsNullOrWhiteSpace(boundary.RequestNodeId) ||
            boundary.AcceptedAt is not { } acceptedAt || boundary.DeadlineAt is not { } deadline ||
            acceptedAt < boundary.PreparedAt || classifiedAt < acceptedAt || classifiedAt >= deadline ||
            submissions.Count != 1 || comments.Count != 0)
            return false;

        var submission = submissions[0];
        if (!submission.TryGetProperty("is_copilot_code_review", out var copilot) ||
            copilot.ValueKind != JsonValueKind.True ||
            !GitHubReviewAdapter.VerifiedReviewer(submission.GetProperty("user")) ||
            boundary.ExistingReviewIds.Contains(submission.GetProperty("id").GetInt64()) ||
            !submission.TryGetProperty("submitted_at", out var at) ||
            !at.TryGetDateTimeOffset(out var submitted) ||
            submitted < acceptedAt || submitted > classifiedAt || submitted >= deadline)
            return false;

        return submission.GetProperty("state").GetString() switch
        {
            "APPROVED" => true,
            "COMMENTED" => HasCleanSummary(submission),
            _ => false
        };
    }

    private static bool HasCleanSummary(JsonElement submission)
    {
        if (!submission.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
            return false;
        var lines = body.GetString()!.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // This is an allowlist of the observed Copilot summary, not a severity parser.
        // An unfamiliar template or contradictory heading must retain the human gate.
        return lines.Length >= 3 && lines[0] == "<!-- ccr-overview-v2 -->" &&
            lines[1] == "### \uD83D\uDFE2 Approval recommended" &&
            lines.Count(line => line.StartsWith("### ", StringComparison.Ordinal)) == 1 &&
            lines.Any(line => line is "**No open issues**" or "**No open issues** \uD83C\uDF89");
    }
}
