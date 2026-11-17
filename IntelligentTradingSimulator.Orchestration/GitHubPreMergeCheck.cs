using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record PreMergeCheckResult(
    string StateRevision, int Issue, int PullRequest, string HeadSha, string TaskId,
    string CiRunId, int ReviewRound, IReadOnlyList<string> ManualChecks)
{
    public bool MergeApproved => false;
    public bool AtomicOrEnforced => false;
}

public sealed class PreMergeCheckException(string message) : Exception(message);

/// <summary>Read-only evidence check; it never grants merge permission.</summary>
public sealed class GitHubPreMergeCheck(GitHubRepositoryApi api, IStateStore store, ICandidateShaValidator candidate)
{
    public async Task<PreMergeCheckResult> CheckAsync(int issueNumber, CancellationToken token = default)
    {
        if (issueNumber <= 0) throw new PreMergeCheckException("Invalid Issue number.");
        await api.VerifyRepositoryAsync(token);
        var snapshot = await store.LoadAsync(token);
        var state = snapshot.State;
        if (snapshot.Revision is null || state.Repository != api.Repository ||
            !state.Issues.TryGetValue(issueNumber, out var issue) || issue.Number != issueNumber)
            throw new PreMergeCheckException("The canonical Issue and state revision are unavailable.");

        if (issue.Phase != Phase.HUMAN_REVIEW || issue.HoldReason != "Awaiting human approval." ||
            !issue.ReactivationRequired || issue.ActiveAuthorized || issue.Paused || issue.ClosurePending ||
            issue.PullRequest is not > 0 || issue.HeadSha is not { Length: 40 } sha ||
            !sha.All(Uri.IsHexDigit) || issue.ActiveTaskId is not { Length: > 0 } taskId ||
            issue.PendingFeedbackEventIds.Count != 0 ||
            state.Outbox.Values.Any(effect => effect.IssueNumber == issueNumber &&
                effect.Status is DeliveryStatus.Pending or DeliveryStatus.Executing or DeliveryStatus.Unknown))
            throw new PreMergeCheckException("The canonical Issue is not settled for human review.");
        if (state.Issues.Values.Any(other => other.Number != issue.Number &&
                (other.PullRequest == issue.PullRequest || other.HeadSha == sha)))
            throw new PreMergeCheckException("Another canonical Issue shares the PR or head SHA.");

        if (RequiredStatusPolicy.Desired(state, issue).State != "success" ||
            issue.Ci is not { RunId: { Length: > 0 } runId } ||
            issue.Review is not { Classification: ReviewClassification.Accepted,
                LifecycleState: ReviewLifecycleState.Completed, RequestBoundary: { Confirmed: true } boundary } review ||
            review.TaskId != taskId || review.Round != issue.Round || review.Sha != sha ||
            string.IsNullOrWhiteSpace(review.SourceEvidence))
            throw new PreMergeCheckException("The canonical task, CI and accepted review do not match.");

        var classifications = issue.Classifications.Values.Where(classification =>
            classification.Classification == ReviewClassification.Accepted &&
            classification.Round == issue.Round && classification.Sha == sha &&
            classification.Author.Kind == ActorKind.Human &&
            issue.Handoffs.TryGetValue(classification.HandoffId, out var handoff) &&
            handoff.Generation == issue.Generation && handoff.TaskId == taskId &&
            handoff.ReviewId == review.ReviewId && handoff.Round == issue.Round && handoff.Sha == sha &&
            handoff.ClassificationEventId == classification.EventId).ToList();
        if (classifications.Count > 1)
            throw new PreMergeCheckException("The exact review has ambiguous human classifications.");

        await VerifyLiveIssueAndPullAsync(issue, token);
        await VerifyAsync("Task", () => candidate.ValidateCandidateShaAsync(state, issue, token));
        await VerifyAsync("Managed CI", () => GitHubRequiredStatusAdapter.VerifyGreenRunAsync(api, issue, token));
        await VerifyAsync("Review", () => VerifyReviewEvidenceAsync(issue, review, boundary,
            classifications.Count == 1, token));

        var latest = await store.LoadAsync(token);
        if (latest.Revision != snapshot.Revision)
            throw new PreMergeCheckException("Canonical state changed during the check.");
        await VerifyLiveIssueAndPullAsync(issue, token);
        return new(snapshot.Revision, issueNumber, issue.PullRequest.Value, sha, taskId, runId, issue.Round,
        [
            "Verify ordinary Build and test on the current head.",
            "Verify the latest Agent managed CI status on that SHA is success from the trusted publisher.",
            "Inspect ambiguous ownership, reused SHAs and uncertain or late status writes outside the canonical snapshot.",
            "Inspect current review threads, findings and human feedback; the review API does not prove request-to-round correlation.",
            "Recheck the live PR and canonical state immediately before the separate human merge decision."
        ]);
    }

    private static async Task VerifyAsync(string stage, Func<Task> read)
    {
        try
        {
            await read();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or HttpRequestException or
            FormatException or OverflowException)
        {
            throw new PreMergeCheckException($"{stage} evidence could not be verified ({exception.GetType().Name}).");
        }
    }

    private async Task VerifyLiveIssueAndPullAsync(IssueState issue, CancellationToken token)
    {
        using var issueResponse = await api.GetAsync($"issues/{issue.Number}", token);
        var liveIssue = issueResponse.RootElement;
        using var pullResponse = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        var pull = pullResponse.RootElement;
        if (liveIssue.GetProperty("number").GetInt32() != issue.Number ||
            liveIssue.GetProperty("state").GetString() != "open" ||
            liveIssue.TryGetProperty("pull_request", out _) ||
            pull.GetProperty("number").GetInt32() != issue.PullRequest ||
            pull.GetProperty("state").GetString() != "open" ||
            pull.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.GetProperty("head").GetProperty("sha").GetString() != issue.HeadSha)
            throw new PreMergeCheckException("The live Issue or PR mapping/head changed.");
    }

    private async Task VerifyReviewEvidenceAsync(IssueState issue, ReviewIdentity review,
        ReviewRequestBoundary boundary, bool humanClassified, CancellationToken token)
    {
        using var evidence = JsonDocument.Parse(review.SourceEvidence!);
        var root = evidence.RootElement;
        if (root.GetProperty("request").GetString() != review.ReviewId)
            throw new PreMergeCheckException("Stored review evidence does not match the request.");
        var recorded = root.GetProperty("observedReviews").EnumerateArray().ToList();
        if (recorded.Count == 0 || recorded.Count > OrchestrationReducer.MaximumIndexEntries)
            throw new PreMergeCheckException("Stored review submissions are missing or ambiguous.");
        var live = await api.ListAsync($"pulls/{issue.PullRequest}/reviews", token);
        var ids = new HashSet<long>();
        foreach (var item in recorded)
        {
            var id = item.GetProperty("id").GetInt64();
            if (!ids.Add(id) || boundary.ExistingReviewIds.Contains(id) ||
                !MatchesReview(item, issue, boundary))
                throw new PreMergeCheckException("Stored review submission identity is inconsistent.");
            var matches = live.Where(candidate => candidate.GetProperty("id").GetInt64() == id).ToList();
            if (matches.Count != 1 || !MatchesReview(matches[0], issue, boundary))
                throw new PreMergeCheckException("The recorded review submission is no longer current.");
        }
        if (!humanClassified)
        {
            if (!root.TryGetProperty("policy", out var policy) || policy.GetString() != GitHubCleanReviewPolicy.Name ||
                !root.TryGetProperty("classifiedAt", out var at) || !at.TryGetDateTimeOffset(out var classifiedAt) ||
                review.Findings.Count != 0 ||
                !GitHubCleanReviewPolicy.Matches(boundary, recorded,
                    root.GetProperty("observedComments").EnumerateArray().ToList(), classifiedAt))
                throw new PreMergeCheckException("The exact review has neither human classification nor verified automatic acceptance.");

            var current = live.Where(item =>
                !boundary.ExistingReviewIds.Contains(item.GetProperty("id").GetInt64()) &&
                item.GetProperty("user").ValueKind == JsonValueKind.Object &&
                GitHubReviewAdapter.VerifiedReviewer(item.GetProperty("user")) &&
                item.GetProperty("commit_id").GetString() == review.Sha).ToList();
            var comments = await api.ListAsync($"pulls/{issue.PullRequest}/reviews/{recorded[0].GetProperty("id").GetInt64()}/comments", token);
            if (!GitHubCleanReviewPolicy.Matches(boundary, current, comments, classifiedAt))
                throw new PreMergeCheckException("Automatically accepted review is no longer unambiguous and clean.");
        }
    }

    private bool MatchesReview(JsonElement item, IssueState issue, ReviewRequestBoundary boundary)
    {
        var actor = item.GetProperty("user");
        return item.GetProperty("id").GetInt64() > 0 &&
            actor.GetProperty("id").GetInt64() == boundary.ReviewerId &&
            actor.GetProperty("node_id").GetString() == boundary.ReviewerNodeId &&
            actor.GetProperty("type").GetString() == "Bot" &&
            item.GetProperty("commit_id").GetString() == issue.HeadSha &&
            item.GetProperty("pull_request_url").GetString() ==
                $"https://api.github.com/repos/{api.Repository}/pulls/{issue.PullRequest}" &&
            item.GetProperty("state").GetString() is "APPROVED" or "COMMENTED" or "CHANGES_REQUESTED" &&
            item.GetProperty("submitted_at").GetDateTimeOffset() >= boundary.PreparedAt;
    }
}
