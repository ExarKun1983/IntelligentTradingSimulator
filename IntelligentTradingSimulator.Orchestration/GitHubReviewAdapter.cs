using System.Net;
using System.Text;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubReviewApproval(bool ActivationApproved = false, bool ApiPolicyAndCredentialVerified = false,
    bool NoAdditionalChargesVerified = false, bool AutomaticReviewsDisabledVerified = false)
{
    public bool AllowsReviews => ActivationApproved && ApiPolicyAndCredentialVerified &&
        NoAdditionalChargesVerified && AutomaticReviewsDisabledVerified;
}

/// <summary>Requests Copilot review; REST observations require human round/classification confirmation.</summary>
public sealed class GitHubReviewAdapter(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null,
    GitHubReviewApproval? approval = null, ICandidateShaValidator? candidateShaValidator = null) : IEffectAdapter
{
    private const string Reviewer = "copilot-pull-request-reviewer[bot]";
    private const long ReviewerId = 175728472;
    private const string ReviewerNodeId = "BOT_kgDOCnlnWA";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private static string Receipt(string id) => "review-request:" + id;

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        if (approval?.AllowsReviews != true)
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPrerequisitesUnverified);
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        if (issue.Review!.RequestBoundary is not null)
            throw new InvalidDataException("Prepared review requests are lookup-only, never re-created.");
        ReviewRequestBoundary boundary;
        try
        {
            boundary = await ReadPreflightAsync(state, issue, intent, token);
        }
        catch (ReviewIdentityException)
        {
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewIdentityMismatch);
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPreflightRejected);
        }
        await SaveBoundaryAsync(intent, boundary, token);
        // Revalidate after persistence and immediately before the non-idempotent mutation.
        var latest = (await store.LoadAsync(token)).State;
        var latestIssue = ValidateIntent(latest, intent);
        ValidateEligibility(latest, latestIssue, intent);
        await ValidateCandidateShaAsync(latest, latestIssue, token);
        if (!await LiveEligibleAsync(latestIssue, token)) throw new InvalidDataException("Review live Issue/PR/head is ineligible.");
        using var response = await api.WriteAsync(HttpMethod.Post, $"pulls/{latestIssue.PullRequest}/requested_reviewers",
            new { reviewers = new[] { Reviewer } }, token, expectedStatus: HttpStatusCode.Created);
        ValidatePull(response.RootElement, latestIssue);
        var requested = response.RootElement.GetProperty("requested_reviewers").EnumerateArray().ToList();
        // Copilot can start immediately and be absent from the returned pending-reviewer list.
        if (requested.Count != 0 && !requested.Any(candidate => ActorMatches(candidate, boundary)))
            throw new InvalidDataException("Review request acknowledgement lacks the verified reviewer.");
        await SaveBoundaryAsync(intent, boundary with { Confirmed = true }, token);
        // Local durable acknowledgement identity, explicitly NOT a GitHub review/job ID.
        return new(DeliveryStatus.Succeeded, Receipt(intent.Id));
    }

    private async Task<ReviewRequestBoundary> ReadPreflightAsync(RepositoryState state, IssueState issue,
        EffectIntent intent, CancellationToken token)
    {
        ValidateEligibility(state, issue, intent);
        await ValidateCandidateShaAsync(state, issue, token);
        if (!await LiveEligibleAsync(issue, token)) throw new InvalidDataException("Review live Issue/PR/head is ineligible.");
        using var user = await api.GetGlobalAsync("users/" + Uri.EscapeDataString(Reviewer), token);
        var actor = user.RootElement;
        if (!VerifiedReviewer(actor))
            throw new ReviewIdentityException();
        using var pending = await api.GetAsync($"pulls/{issue.PullRequest}/requested_reviewers", token);
        if (pending.RootElement.GetProperty("users").EnumerateArray().Any(candidate =>
                candidate.GetProperty("id").GetInt64() == ReviewerId ||
                candidate.GetProperty("login").GetString() is Reviewer or "Copilot"))
            throw new InvalidDataException("Existing review request requires reconciliation.");
        var baseline = await api.ListAsync($"pulls/{issue.PullRequest}/reviews", token);
        if (baseline.Any(review => review.GetProperty("state").GetString() == "PENDING" &&
                review.GetProperty("user").ValueKind == JsonValueKind.Object &&
                review.GetProperty("user").GetProperty("id").GetInt64() == ReviewerId))
            throw new InvalidDataException("Pending reviewer output predates this request.");
        return new ReviewRequestBoundary(ReviewerId, ReviewerNodeId, clock.GetUtcNow(),
            baseline.Select(review => review.GetProperty("id").GetInt64()).ToList());
    }

    public async Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token)
    {
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        await api.VerifyRepositoryAsync(token);
        // Requested-reviewer presence/absence and new output cannot identify a lost request.
        return issue.Review!.RequestBoundary?.Confirmed == true
            ? new(DeliveryStatus.Succeeded, Receipt(intent.Id)) : new(DeliveryStatus.Unknown);
    }

    public async Task<ReviewAbandonmentEvidence> ReadAbandonmentEvidenceAsync(RepositoryState state,
        IssueState issue, string intentId, string expectedSha, DateTimeOffset observedAt,
        CancellationToken token)
    {
        if (state.Repository != api.Repository || state.AdmittedIssue != issue.Number ||
            issue.Phase != Phase.BLOCKED || issue.HoldReason != "Effect outcome is ambiguous." ||
            !issue.ReactivationRequired || issue.ActiveAuthorized || issue.Paused ||
            issue.Review is not { Classification: null, ReviewId: null } review || review.IntentId != intentId ||
            review.Sha != expectedSha || issue.HeadSha != expectedSha || issue.PullRequest is not { } pullNumber ||
            !state.Outbox.TryGetValue(intentId, out var intent) ||
            intent.Kind != EffectKind.RequestReview || intent.Status != DeliveryStatus.Unknown ||
            intent.IssueNumber != issue.Number || intent.Generation != issue.Generation ||
            intent.TaskId != review.TaskId || intent.Round != review.Round || intent.Sha != review.Sha ||
            review.RequestBoundary?.Confirmed == true)
            throw new InvalidDataException("Review abandonment does not match the canonical uncertain request.");
        await ValidateCandidateShaAsync(state, issue, token);
        await api.VerifyRepositoryAsync(token);
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        if (liveIssue.RootElement.GetProperty("number").GetInt32() != issue.Number ||
            liveIssue.RootElement.GetProperty("state").GetString() != "open" ||
            liveIssue.RootElement.TryGetProperty("pull_request", out _))
            throw new InvalidDataException("Review abandonment requires the exact open Issue.");
        using var pull = await api.GetAsync($"pulls/{pullNumber}", token);
        var pullRoot = pull.RootElement;
        if (pullRoot.GetProperty("number").GetInt32() != pullNumber ||
            pullRoot.GetProperty("state").GetString() != "open" ||
            pullRoot.GetProperty("head").GetProperty("sha").GetString() != expectedSha ||
            pullRoot.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pullRoot.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("Review abandonment requires the mapped open PR at the exact current head.");

        foreach (var candidate in await api.ListRequestedReviewersAsync(pullNumber, token))
        {
            if (!IsReviewerCandidate(candidate)) continue;
            if (!VerifiedReviewer(candidate))
                throw new InvalidDataException("Pending reviewer identity is ambiguous.");
            throw new InvalidOperationException("Copilot review is still requested; do not abandon it.");
        }

        var reviewerIds = new List<long>();
        foreach (var candidate in await api.ListAsync($"pulls/{pullNumber}/reviews", token))
        {
            if (!candidate.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Review abandonment could not verify a review author.");
            if (!IsReviewerCandidate(user)) continue;
            if (!VerifiedReviewer(user))
                throw new InvalidDataException("Historical reviewer identity is ambiguous.");
            var id = candidate.GetProperty("id").GetInt64();
            if (id <= 0) throw new InvalidDataException("Invalid historical reviewer identity.");
            reviewerIds.Add(id);
        }
        if (reviewerIds.Count != 0)
            throw new InvalidOperationException("Copilot review history exists; do not abandon this uncertain request.");
        return new(intentId, pullNumber, expectedSha, observedAt, false, reviewerIds);
    }

    public async Task<OrchestrationEvent?> ReadEventAsync(RepositoryState state, IssueState issue, CancellationToken token)
    {
        if (state.Repository != api.Repository || issue.PullRequest is not { } number ||
            issue.Review is not { Classification: null, RequestBoundary: { Confirmed: true } boundary } review ||
            review.ReviewId != Receipt(review.IntentId) ||
            !state.Outbox.TryGetValue(review.IntentId, out var intent) || intent.Status != DeliveryStatus.Succeeded ||
            issue.Ci?.Green != true || issue.ActiveTaskId != review.TaskId ||
            !issue.Tasks.TryGetValue(review.TaskId, out var task) || task.Status != TaskStatus.Completed ||
            task.FinalSha != review.Sha || issue.HeadSha != review.Sha)
            return null;
        await ValidateCandidateShaAsync(state, issue, token);
        if (!await LiveEligibleAsync(issue, token)) return null;
        var reviews = await api.ListAsync($"pulls/{number}/reviews", token);
        var observed = new List<JsonElement>();
        foreach (var candidate in reviews)
        {
            if (boundary.ExistingReviewIds.Contains(candidate.GetProperty("id").GetInt64())) continue;
            if (candidate.GetProperty("user").ValueKind != JsonValueKind.Object ||
                !ActorMatches(candidate.GetProperty("user"), boundary)) continue;
            if (candidate.GetProperty("commit_id").GetString() != review.Sha ||
                candidate.GetProperty("state").GetString() == "PENDING") continue;
            if (candidate.GetProperty("id").GetInt64() <= 0 ||
                candidate.GetProperty("pull_request_url").GetString() != $"https://api.github.com/repos/{api.Repository}/pulls/{number}")
                throw new InvalidDataException("Review submission scope mismatch.");
            if (!candidate.TryGetProperty("submitted_at", out var at) || !at.TryGetDateTimeOffset(out var submitted))
                throw new InvalidDataException("Review submission time is unavailable.");
            if (submitted < boundary.PreparedAt) continue;
            observed.Add(candidate);
        }
        if (observed.Count == 0) return null;
        var comments = new List<JsonElement>();
        foreach (var candidate in observed)
        {
            var id = candidate.GetProperty("id").GetInt64();
            var entries = await api.ListAsync($"pulls/{number}/reviews/{id}/comments", token);
            foreach (var entry in entries)
            {
                if (entry.GetProperty("id").GetInt64() <= 0 || entry.GetProperty("pull_request_review_id").GetInt64() != id ||
                    entry.GetProperty("pull_request_url").GetString() != $"https://api.github.com/repos/{api.Repository}/pulls/{number}" ||
                    !ActorMatches(entry.GetProperty("user"), boundary))
                    throw new InvalidDataException("Review comment identity mismatch.");
                comments.Add(entry);
                if (comments.Count > OrchestrationReducer.MaximumIndexEntries)
                    throw new InvalidDataException("Review evidence capacity exceeded.");
            }
        }
        var evidence = JsonSerializer.Serialize(new
        {
            reason = "REST has no request-to-submission round token; prose is not classified approval or blockers. Human confirmation required.",
            request = Receipt(intent.Id), observedReviews = observed, observedComments = comments
        });
        if (Encoding.UTF8.GetByteCount(evidence) > 1024 * 1024)
            throw new InvalidDataException("Review evidence capacity exceeded.");
        return new($"review-observed:{api.RepositoryId}:{issue.Generation}:{intent.Id}", api.Repository, issue.Number,
            state.Generation, issue.Generation, new(Reviewer, ActorKind.ReviewService, boundary.ReviewerId,
                boundary.ReviewerNodeId, "Bot"), boundary.PreparedAt,
            new ReviewResult(Receipt(intent.Id), review.TaskId, review.Round, number, review.Sha,
                ReviewClassification.NeedsHuman, [], [], evidence));
    }

    private IssueState ValidateIntent(RepositoryState state, EffectIntent intent)
    {
        if (state.Repository != api.Repository || intent.Kind != EffectKind.RequestReview ||
            !state.Outbox.TryGetValue(intent.Id, out var persisted) || persisted.Digest != intent.Digest ||
            persisted.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown) ||
            persisted.IssueNumber != intent.IssueNumber || persisted.ExternalId != intent.ExternalId ||
            persisted.Generation != intent.Generation || persisted.TaskId != intent.TaskId ||
            persisted.Round != intent.Round || persisted.Sha != intent.Sha ||
            !state.Issues.TryGetValue(intent.IssueNumber, out var issue) || issue.Generation != intent.Generation ||
            issue.Review?.IntentId != intent.Id || issue.Review.TaskId != intent.TaskId ||
            issue.Review.Round != intent.Round || issue.Review.Sha != intent.Sha)
            throw new InvalidDataException("Review effect does not match the canonical executing request.");
        return issue;
    }

    private static void ValidateEligibility(RepositoryState state, IssueState issue, EffectIntent intent)
    {
        if (state.AdmittedIssue != issue.Number || issue.Phase != Phase.REVIEW_PENDING || !issue.ActiveAuthorized ||
            issue.Paused || issue.ReactivationRequired || issue.ClosurePending || issue.PendingFeedbackEventIds.Count != 0 ||
            issue.HeadSha != intent.Sha || issue.ActiveTaskId != intent.TaskId ||
            !issue.Tasks.TryGetValue(intent.TaskId!, out var task) || task.Status != TaskStatus.Completed ||
            !task.LaunchConfirmed || task.Round != intent.Round || task.FinalSha != intent.Sha ||
            issue.Ci is not { Green: true } ci || ci.TaskId != task.Id || ci.Round != intent.Round || ci.Sha != intent.Sha ||
            !state.Outbox.TryGetValue(ci.IntentId, out var ciEffect) || ciEffect.Status != DeliveryStatus.Succeeded ||
            ciEffect.ExternalId != ci.RunId || string.IsNullOrWhiteSpace(ci.RunId))
            throw new InvalidDataException("Review requires completed coding and exact-SHA green CI with no feedback or hold.");
    }

    private Task ValidateCandidateShaAsync(RepositoryState state, IssueState issue, CancellationToken token) =>
        candidateShaValidator?.ValidateCandidateShaAsync(state, issue, token) ??
        throw new InvalidDataException("Candidate SHA validator is unavailable.");

    private async Task<bool> LiveEligibleAsync(IssueState issue, CancellationToken token)
    {
        await api.VerifyRepositoryAsync(token);
        using var liveIssue = await api.GetAsync($"issues/{issue.Number}", token);
        if (liveIssue.RootElement.GetProperty("number").GetInt32() != issue.Number ||
            liveIssue.RootElement.TryGetProperty("pull_request", out _))
            throw new InvalidDataException("Review Issue identity mismatch.");
        using var pull = await api.GetAsync($"pulls/{issue.PullRequest}", token);
        var root = pull.RootElement;
        if (root.GetProperty("number").GetInt32() != issue.PullRequest ||
            root.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            root.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("Review PR repository identity mismatch.");
        return liveIssue.RootElement.GetProperty("state").GetString() == "open" &&
            root.GetProperty("state").GetString() == "open" &&
            root.GetProperty("head").GetProperty("sha").GetString() == issue.HeadSha;
    }

    private void ValidatePull(JsonElement pull, IssueState issue)
    {
        if (pull.GetProperty("number").GetInt32() != issue.PullRequest || pull.GetProperty("state").GetString() != "open" ||
            pull.GetProperty("head").GetProperty("sha").GetString() != issue.HeadSha ||
            pull.GetProperty("head").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId ||
            pull.GetProperty("base").GetProperty("repo").GetProperty("id").GetInt64() != api.RepositoryId)
            throw new InvalidDataException("Review live PR/head/repository mismatch.");
    }

    private static bool VerifiedReviewer(JsonElement user) =>
        user.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && number == ReviewerId &&
        user.TryGetProperty("node_id", out var node) && node.GetString() == ReviewerNodeId &&
        user.TryGetProperty("login", out var login) && login.GetString() is Reviewer or "Copilot" &&
        user.TryGetProperty("type", out var type) && type.GetString() == "Bot";

    private static bool IsReviewerCandidate(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object ||
            !user.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number ||
            !id.TryGetInt64(out var number) || number <= 0 ||
            !user.TryGetProperty("node_id", out var node) || node.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(node.GetString()) ||
            !user.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(login.GetString()) ||
            !user.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(type.GetString()))
            throw new InvalidDataException("Reviewer identity is incomplete.");

        return number == ReviewerId || node.GetString() == ReviewerNodeId || login.GetString() is Reviewer or "Copilot";
    }

    private static bool ActorMatches(JsonElement user, ReviewRequestBoundary boundary) =>
        boundary.ReviewerId == ReviewerId && boundary.ReviewerNodeId == ReviewerNodeId && VerifiedReviewer(user);

    private sealed class ReviewIdentityException : Exception;

    private async Task SaveBoundaryAsync(EffectIntent intent, ReviewRequestBoundary boundary, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            var issue = ValidateIntent(snapshot.State, intent);
            if (!boundary.Confirmed && issue.Review!.RequestBoundary is not null)
                throw new InvalidDataException("Another executor already prepared this review request.");
            var next = new OrchestrationReducer().RecordReviewBoundary(snapshot.State, intent.Id, boundary);
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Review boundary publication conflict; request must remain lookup-only.");
    }
}
