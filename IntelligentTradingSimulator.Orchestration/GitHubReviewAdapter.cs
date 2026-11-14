using System.Net;
using System.Text;
using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record GitHubReviewApproval(bool ActivationApproved = false, bool ApiPolicyAndCredentialVerified = false,
    bool NoAdditionalChargesVerified = false, bool AutomaticReviewsDisabledVerified = false)
{
    public TimeSpan ReviewTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public bool AutoRepairFindings { get; init; }

    public bool AllowsReviews => ActivationApproved && ApiPolicyAndCredentialVerified &&
        NoAdditionalChargesVerified && AutomaticReviewsDisabledVerified &&
        ReviewTimeout >= TimeSpan.FromMinutes(1) && ReviewTimeout <= TimeSpan.FromMinutes(30);
}

/// <summary>Requests Copilot review; an opt-in exact-head policy can classify fresh inline findings as blocking.</summary>
public sealed class GitHubReviewAdapter(GitHubRepositoryApi api, IStateStore store, TimeProvider? clock = null,
    GitHubReviewApproval? approval = null, ICandidateShaValidator? candidateShaValidator = null,
    GitHubReviewRequestDiagnostics? diagnostics = null) : IEffectAdapter
{
    private const string Reviewer = "copilot-pull-request-reviewer[bot]";
    private const long ReviewerId = 175728472;
    private const string ReviewerNodeId = "BOT_kgDOCnlnWA";
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private static string Receipt(string id) => "review-request:" + id;

    public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
    {
        diagnostics?.Begin(intent);
        try
        {
            return await ExecuteRequestAsync(intent, token);
        }
        catch (Exception exception)
        {
            diagnostics?.RecordFailure(exception);
            throw;
        }
    }

    private async Task<EffectOutcome> ExecuteRequestAsync(EffectIntent intent, CancellationToken token)
    {
        if (approval?.AllowsReviews != true)
        {
            diagnostics?.Refuse("prerequisites");
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPrerequisitesUnverified);
        }
        diagnostics?.Stage("canonical_intent");
        var state = (await store.LoadAsync(token)).State;
        var issue = ValidateIntent(state, intent);
        if (issue.Review!.RequestBoundary is not null)
            throw new InvalidDataException("Prepared review requests are lookup-only, never re-created.");
        ReviewRequestBoundary boundary;
        try
        {
            boundary = await ReadPreflightAsync(state, issue, intent, token);
        }
        catch (ReviewIdentityException exception)
        {
            diagnostics?.RecordFailure(exception);
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewIdentityMismatch);
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            diagnostics?.RecordFailure(exception);
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPreflightRejected);
        }
        diagnostics?.Stage("save_boundary");
        await SaveBoundaryAsync(intent, boundary, token);
        // Revalidate after persistence and immediately before the non-idempotent mutation.
        diagnostics?.Stage("revalidate_canonical");
        var latest = (await store.LoadAsync(token)).State;
        var latestIssue = ValidateIntent(latest, intent);
        ValidateEligibility(latest, latestIssue, intent);
        diagnostics?.Stage("revalidate_candidate");
        await ValidateCandidateShaAsync(latest, latestIssue, token);
        diagnostics?.Stage("revalidate_live");
        if (!await LiveEligibleAsync(latestIssue, token)) throw new InvalidDataException("Review live Issue/PR/head is ineligible.");
        for (var attempt = 1; ; attempt++)
        {
            diagnostics?.BeginAttempt(attempt);
            var response = await api.RequestCopilotCodeReviewAsync(latestIssue.PullRequest!.Value, token,
                metadata => diagnostics?.ObserveResponseMetadata(metadata, attempt));
            if (response.StatusCode == (int)HttpStatusCode.Conflict)
                response = response with { State = "PENDING" };
            diagnostics?.ObserveResponse(response, attempt);
            if (response.StatusCode == (int)HttpStatusCode.Created)
            {
                var acceptedAt = clock.GetUtcNow();
                diagnostics?.Stage("acknowledgement_save");
                await SaveBoundaryAsync(intent, boundary with
                {
                    Confirmed = true,
                    RequestNodeId = response.NodeId,
                    AcceptedAt = acceptedAt,
                    DeadlineAt = acceptedAt + approval!.ReviewTimeout
                }, token);
                diagnostics?.Complete();
                return new(DeliveryStatus.Succeeded, Receipt(intent.Id));
            }

            if (response.StatusCode == (int)HttpStatusCode.Conflict)
            {
                var acceptedAt = clock.GetUtcNow();
                diagnostics?.Stage("acknowledgement_save");
                await SaveBoundaryAsync(intent, boundary with
                {
                    Confirmed = true,
                    AcceptedAt = acceptedAt,
                    DeadlineAt = acceptedAt + approval!.ReviewTimeout,
                    AlreadyPending = true
                }, token);
                diagnostics?.Complete("already_pending");
                return new(DeliveryStatus.Succeeded, Receipt(intent.Id));
            }

            if (response.StatusCode == 429 && attempt < 3)
            {
                var delay = response.RetryAfter ?? TimeSpan.FromSeconds(attempt);
                if (delay <= TimeSpan.FromSeconds(30))
                {
                    diagnostics?.Stage("retry_wait");
                    await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, token);
                    diagnostics?.Stage("retry_revalidate");
                    if (await RetryEligibleAsync(intent, token))
                        continue;
                }
            }

            if (response.StatusCode is not (401 or 403 or 404 or 422 or 429))
                throw new HttpRequestException("Review request outcome is ambiguous.");

            var failureReason = response.FailureReason ?? $"HTTP_{response.StatusCode}";
            diagnostics?.RecordApiFailure(failureReason);
            await SaveFailureReasonAsync(intent, failureReason, token);
            return new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewRequestRejected);
        }
    }

    private async Task<bool> RetryEligibleAsync(EffectIntent intent, CancellationToken token)
    {
        // A failed revalidation leaves the preceding rate-limit rejection as the definitive outcome.
        try
        {
            var retryState = (await store.LoadAsync(token)).State;
            var retryIssue = ValidateIntent(retryState, intent);
            ValidateEligibility(retryState, retryIssue, intent);
            await ValidateCandidateShaAsync(retryState, retryIssue, token);
            return await LiveEligibleAsync(retryIssue, token);
        }
        catch (Exception exception) when (exception is InvalidDataException or HttpRequestException or
            JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<ReviewRequestBoundary> ReadPreflightAsync(RepositoryState state, IssueState issue,
        EffectIntent intent, CancellationToken token)
    {
        diagnostics?.Stage("preflight_eligibility");
        ValidateEligibility(state, issue, intent);
        diagnostics?.Stage("preflight_candidate");
        await ValidateCandidateShaAsync(state, issue, token);
        diagnostics?.Stage("preflight_live");
        if (!await LiveEligibleAsync(issue, token)) throw new InvalidDataException("Review live Issue/PR/head is ineligible.");
        diagnostics?.Stage("preflight_reviewer_identity");
        using var user = await api.GetGlobalAsync("users/" + Uri.EscapeDataString(Reviewer), token);
        var actor = user.RootElement;
        if (!VerifiedReviewer(actor))
            throw new ReviewIdentityException();
        diagnostics?.Stage("preflight_review_baseline");
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
            review.LifecycleState == ReviewLifecycleState.Failed ||
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
        if (observed.Count == 0)
        {
            if (review.LifecycleState is ReviewLifecycleState.Requested or ReviewLifecycleState.Pending &&
                boundary.DeadlineAt is { } deadline && clock.GetUtcNow() >= deadline)
                return ReviewProgressEvent(state, issue, review, number, clock.GetUtcNow(), timedOut: true);
            if (review.LifecycleState == ReviewLifecycleState.Requested)
                return ReviewProgressEvent(state, issue, review, number, clock.GetUtcNow(), timedOut: false);
            return null;
        }
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
        var findings = ReadAutomaticFindings(issue, boundary, observed, comments, number);
        var classification = findings.Count > 0 ? ReviewClassification.Blocking : ReviewClassification.NeedsHuman;
        var evidence = JsonSerializer.Serialize(new
        {
            reason = classification == ReviewClassification.Blocking
                ? "Opt-in exact-head inline findings require repair; provider request-to-submission correlation is not asserted."
                : "REST has no request-to-submission round token; human classification is required for this output.",
            policy = approval?.AutoRepairFindings == true ? "exact-head-inline-findings-v1" : "human-classification",
            request = Receipt(intent.Id), observedReviews = observed, observedComments = comments
        });
        if (Encoding.UTF8.GetByteCount(evidence) > 1024 * 1024)
            throw new InvalidDataException("Review evidence capacity exceeded.");
        return new($"review-observed:{api.RepositoryId}:{issue.Generation}:{intent.Id}", api.Repository, issue.Number,
            state.Generation, issue.Generation, new(Reviewer, ActorKind.ReviewService, boundary.ReviewerId,
                boundary.ReviewerNodeId, "Bot"), boundary.PreparedAt,
            new ReviewResult(Receipt(intent.Id), review.TaskId, review.Round, number, review.Sha,
                classification, findings, [], evidence));
    }

    private List<Finding> ReadAutomaticFindings(IssueState issue, ReviewRequestBoundary boundary,
        List<JsonElement> observed, List<JsonElement> comments, int pullNumber)
    {
        if (approval is not { AllowsReviews: true, AutoRepairFindings: true } ||
            boundary.AlreadyPending || string.IsNullOrWhiteSpace(boundary.RequestNodeId) ||
            boundary.AcceptedAt is not { } acceptedAt || observed.Count != 1 || comments.Count == 0 ||
            issue.Phase != Phase.REVIEW_PENDING || !issue.ActiveAuthorized || issue.Paused ||
            issue.ReactivationRequired || issue.PendingFeedbackEventIds.Count != 0 ||
            issue.Review!.LifecycleState == ReviewLifecycleState.TimedOut ||
            boundary.DeadlineAt is not { } deadline || clock.GetUtcNow() >= deadline)
            return [];

        var submission = observed[0];
        var submitted = submission.GetProperty("submitted_at").GetDateTimeOffset();
        if (!submission.TryGetProperty("is_copilot_code_review", out var copilot) ||
            copilot.ValueKind != JsonValueKind.True ||
            submission.GetProperty("state").GetString() is not ("COMMENTED" or "CHANGES_REQUESTED") ||
            submitted < acceptedAt || submitted >= deadline)
            return [];

        var findings = issue.Review.Findings.ToList();
        var ids = new HashSet<long>();
        foreach (var comment in comments)
        {
            if (!ids.Add(comment.GetProperty("id").GetInt64()) ||
                !comment.TryGetProperty("commit_id", out var commit) || commit.GetString() != issue.HeadSha ||
                !comment.TryGetProperty("original_commit_id", out var original) || original.GetString() != issue.HeadSha ||
                comment.TryGetProperty("in_reply_to_id", out var reply) && reply.ValueKind != JsonValueKind.Null ||
                !comment.TryGetProperty("line", out var line) || line.ValueKind != JsonValueKind.Number ||
                !line.TryGetInt32(out var lineNumber) || lineNumber <= 0 ||
                !comment.TryGetProperty("side", out var side) || side.GetString() != "RIGHT" ||
                !comment.TryGetProperty("path", out var path) || string.IsNullOrWhiteSpace(path.GetString()) ||
                !comment.TryGetProperty("body", out var body) || string.IsNullOrWhiteSpace(body.GetString()) ||
                !comment.TryGetProperty("created_at", out var created) || created.ValueKind != JsonValueKind.String ||
                !created.TryGetDateTimeOffset(out var createdAt) ||
                !comment.TryGetProperty("updated_at", out var updated) || updated.ValueKind != JsonValueKind.String ||
                !updated.TryGetDateTimeOffset(out var updatedAt) ||
                createdAt < acceptedAt || createdAt > submitted || updatedAt != createdAt)
                return [];

            var id = comment.GetProperty("id").GetInt64();
            if (findings.Any(finding => finding.Id == $"copilot-review-comment:{id}")) return [];
            findings.Add(new($"copilot-review-comment:{id}", $"{path.GetString()}:{lineNumber}\n{body.GetString()}",
                $"https://github.com/{api.Repository}/pull/{pullNumber}#discussion_r{id}"));
        }
        return findings;
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
        if (state.Outbox.Values.Any(previous => previous.Id != intent.Id &&
            previous.Kind == EffectKind.RequestReview && previous.Sha == intent.Sha &&
            previous.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown or DeliveryStatus.Succeeded &&
            state.Issues.TryGetValue(previous.IssueNumber, out var previousIssue) &&
            previousIssue.PullRequest == issue.PullRequest))
            throw new InvalidDataException("A review request for this PR and commit already exists or is uncertain.");
    }

    private Task ValidateCandidateShaAsync(RepositoryState state, IssueState issue, CancellationToken token) =>
        candidateShaValidator?.ValidateCandidateShaAsync(state, issue, token) ??
        throw new InvalidDataException("Candidate SHA validator is unavailable.");

    private OrchestrationEvent ReviewProgressEvent(RepositoryState state, IssueState issue, ReviewIdentity review,
        int pullNumber, DateTimeOffset occurredAt, bool timedOut)
    {
        var data = timedOut
            ? (EventData)new ReviewTimedOut(review.IntentId, pullNumber, review.Sha)
            : new ReviewPending(review.IntentId, pullNumber, review.Sha);
        var kind = timedOut ? "timed-out" : "pending";
        return new($"copilot-review-{kind}:{api.RepositoryId}:{issue.Generation}:{review.IntentId}",
            api.Repository, issue.Number, state.Generation, issue.Generation,
            new(Reviewer, ActorKind.ReviewService, ReviewerId, ReviewerNodeId, "Bot"), occurredAt, data);
    }

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

    private async Task SaveFailureReasonAsync(EffectIntent intent, string failureReason, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(token);
            ValidateIntent(snapshot.State, intent);
            var next = new OrchestrationReducer().RecordReviewRejection(snapshot.State, intent.Id, failureReason,
                clock.GetUtcNow());
            if (await store.TrySaveAsync(snapshot, next, token)) return;
        }
        throw new InvalidOperationException("Review failure publication conflict; inspect canonical state.");
    }
}
