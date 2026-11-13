using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubReviewIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly string Sha = new('a', 40);
    private static readonly GitHubReviewApproval Approved = new(true, true, true, true);
    private static GitHubReviewAdapter CreateAdapter(HttpClient client, Store store) =>
        new(new(client, "owner/repo", 1), store, new Clock(), Approved, new CandidateValidator());

    [Fact]
    public async Task DedicatedApiAcknowledgementTracksPendingThenCompletedReviewForExactHead()
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        using var log = new StringWriter();
        var diagnostics = new GitHubReviewRequestDiagnostics(log);
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            Approved, new CandidateValidator(), diagnostics);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal(1, handler.Posts);
        Assert.Contains("POST /repos/owner/repo/pulls/17/copilot-code-review", handler.Requests);
        Assert.Equal("RRR_kwDOAA", store.State.Issues[7].Review!.CopilotRequestNodeId);
        Assert.Equal(ReviewLifecycleState.Requested, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal("PENDING", diagnostics.Snapshot!.ResponseState);
        Assert.Equal("RRR_kwDOAA", diagnostics.Snapshot.RequestNodeId);

        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        var pendingEvent = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.IsType<ReviewPending>(pendingEvent!.Data);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State, pendingEvent);
        Assert.Equal(ReviewLifecycleState.Pending, store.State.Issues[7].Review!.LifecycleState);

        handler.HasOutput = true;
        var completedEvent = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.IsType<ReviewResult>(completedEvent!.Data);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State, completedEvent);
        Assert.Equal(ReviewLifecycleState.Completed, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal(1, handler.Posts);
        Assert.DoesNotContain("Review body preserved", log.ToString());
    }

    [Fact]
    public async Task ReviewTimeoutIsPersistedWithoutReissuingTheRequest()
    {
        var state = Eligible();
        var issue = state.Issues[7];
        issue.Review!.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now,
            [100], Confirmed: true, RequestNodeId: "RRR_kwDOAA",
            AcceptedAt: Now.AddMinutes(-2), DeadlineAt: Now.AddMinutes(-1));
        issue.Review.ReviewId = "review-request:review";
        issue.Review.LifecycleState = ReviewLifecycleState.Pending;
        issue.Review.ReviewDeadlineAt = Now.AddMinutes(-1);
        state.Outbox["review"].Status = DeliveryStatus.Succeeded;
        state.Outbox["review"].ExternalId = "review-request:review";
        var store = new Store(state);
        var handler = new Api();
        using var client = handler.Client();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store,
            new Clock(Now), Approved, new CandidateValidator());
        var message = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.IsType<ReviewTimedOut>(message!.Data);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State, message);
        Assert.Equal(ReviewLifecycleState.TimedOut, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task LateReviewResultReleasesAdmissionWithoutClearingTimeoutHold()
    {
        var state = Eligible();
        var issue = state.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.ActiveAuthorized = false;
        issue.ReactivationRequired = true;
        issue.HoldReason = "Copilot review timed out.";
        issue.Review!.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now.AddMinutes(-2),
            [100], Confirmed: true, RequestNodeId: "RRR_kwDOAA",
            AcceptedAt: Now.AddMinutes(-2), DeadlineAt: Now.AddMinutes(-1));
        issue.Review.ReviewId = "review-request:review";
        issue.Review.LifecycleState = ReviewLifecycleState.TimedOut;
        issue.Review.ReviewDeadlineAt = Now.AddMinutes(-1);
        state.Outbox["review"].Status = DeliveryStatus.Succeeded;
        state.Outbox["review"].ExternalId = "review-request:review";
        var store = new Store(state);
        var handler = new Api { HasOutput = true };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var message = await adapter.ReadEventAsync(store.State, issue, default);
        Assert.IsType<ReviewResult>(message!.Data);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State, message);

        Assert.Equal(ReviewLifecycleState.Completed, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal(ReviewClassification.NeedsHuman, store.State.Issues[7].Review!.Classification);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal("Copilot review timed out.", store.State.Issues[7].HoldReason);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Null(store.State.AdmittedIssue);
        Assert.Equal(0, handler.Posts);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "ACTOR_FLAGGED")]
    [InlineData(HttpStatusCode.NotFound, null)]
    [InlineData(HttpStatusCode.UnprocessableEntity, "NO_QUOTA")]
    public async Task DocumentedApiRejectionsAreDefiniteFailures(HttpStatusCode status, string? failureReason)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatus = status, ApiFailureReason = failureReason };
        using var client = handler.Client();
        var diagnostics = new GitHubReviewRequestDiagnostics();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            Approved, new CandidateValidator(), diagnostics);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);

        Assert.Equal(DeliveryStatus.Failed, outcome.Status);
        Assert.Equal(EffectFailureCode.ReviewRequestRejected, outcome.FailureCode);
        Assert.Equal("rejected", diagnostics.Snapshot!.Outcome);
        Assert.Equal(failureReason ?? "HTTP_" + (int)status, diagnostics.Snapshot.FailureReason);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        Assert.Equal(ReviewLifecycleState.Failed, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal(1, handler.Posts);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task ReviewRejectionAndReasonArePersistedAtomically()
    {
        var store = new Store(Eligible());
        var handler = new Api
        {
            ReviewPostStatus = HttpStatusCode.Forbidden,
            ApiFailureReason = "ACTOR_FLAGGED"
        };
        handler.BeforePost = () =>
        {
            store.AfterSave = () =>
            {
                store.AfterSave = null;
                throw new IOException("Lost response after committed rejection.");
            };
        };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        await Assert.ThrowsAsync<IOException>(() =>
            adapter.ExecuteAsync(store.State.Outbox["review"], default));

        Assert.Equal(DeliveryStatus.Failed, store.State.Outbox["review"].Status);
        Assert.Equal(EffectFailureCode.ReviewRequestRejected, store.State.Outbox["review"].FailureCode);
        Assert.NotNull(store.State.Outbox["review"].CompletedAt);
        var review = store.State.Issues[7].Review!;
        Assert.Equal("ACTOR_FLAGGED", review.RequestFailureReason);
        Assert.Equal(ReviewLifecycleState.Failed, review.LifecycleState);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Null(store.State.AdmittedIssue);
        var serialized = StateJson.Serialize(store.State);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review",
            new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewRequestRejected), Now);
        Assert.Equal(serialized, StateJson.Serialize(store.State));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ExplicitTransientRejectionsRetryWithinBoundAndThenConfirm(HttpStatusCode transient)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatuses = [transient, HttpStatusCode.Created] };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);

        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal(2, handler.Posts);
        Assert.Equal(ReviewLifecycleState.Requested, store.State.Issues[7].Review!.LifecycleState);
    }

    [Fact]
    public async Task AmbiguousRetryClearsPriorResponseDiagnosticsBeforeSending()
    {
        var store = new Store(Eligible());
        var handler = new Api
        {
            ReviewPostStatuses = [HttpStatusCode.TooManyRequests, HttpStatusCode.Created],
            LoseReviewPostResponseOn = 2
        };
        using var client = handler.Client();
        var diagnostics = new GitHubReviewRequestDiagnostics();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            Approved, new CandidateValidator(), diagnostics);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            adapter.ExecuteAsync(store.State.Outbox["review"], default));

        Assert.Equal(2, diagnostics.Snapshot!.Attempt);
        Assert.Null(diagnostics.Snapshot.HttpStatus);
        Assert.Null(diagnostics.Snapshot.GitHubRequestId);
        Assert.Null(diagnostics.Snapshot.RequestNodeId);
        Assert.Null(diagnostics.Snapshot.ResponseState);
        Assert.Null(diagnostics.Snapshot.FailureReason);
        Assert.Equal("transport", diagnostics.Snapshot.ErrorCategory);
        Assert.Equal(2, handler.Posts);
    }

    [Fact]
    public async Task HeadChangeDuringRateLimitRetryPersistsRejectionWithoutAnotherPost()
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatuses = [HttpStatusCode.TooManyRequests, HttpStatusCode.Created] };
        handler.BeforePost = () => handler.Corruption = "live-sha";
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);

        Assert.Equal(DeliveryStatus.Failed, outcome.Status);
        Assert.Equal(EffectFailureCode.ReviewRequestRejected, outcome.FailureCode);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(DeliveryStatus.Failed, store.State.Outbox["review"].Status);
        Assert.Equal(EffectFailureCode.ReviewRequestRejected, store.State.Outbox["review"].FailureCode);
        Assert.Equal("HTTP_429", store.State.Issues[7].Review!.RequestFailureReason);
        Assert.Equal(ReviewLifecycleState.Failed, store.State.Issues[7].Review!.LifecycleState);
        Assert.Null(store.State.AdmittedIssue);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task AcknowledgementPersistenceFailureIsNotReportedAsResponseFailure(HttpStatusCode responseStatus)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatus = responseStatus };
        handler.BeforePost = () => store.FailWrites = true;
        using var client = handler.Client();
        var diagnostics = new GitHubReviewRequestDiagnostics();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            Approved, new CandidateValidator(), diagnostics);

        await Assert.ThrowsAsync<IOException>(() =>
            adapter.ExecuteAsync(store.State.Outbox["review"], default));

        Assert.Equal("acknowledgement_save", diagnostics.Snapshot!.Stage);
        Assert.Equal("persistence_or_io", diagnostics.Snapshot.ErrorCategory);
        Assert.Equal((int)responseStatus, diagnostics.Snapshot.HttpStatus);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task AmbiguousHttpResponseNeverTriggersAnotherRequest(HttpStatusCode status)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatus = status };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            adapter.ExecuteAsync(store.State.Outbox["review"], default));
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.State.Outbox["review"], default)).Status);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task ConflictMeansAlreadyPendingAndNeverCreatesAnotherReview()
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewPostStatus = HttpStatusCode.Conflict };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);

        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal(1, handler.Posts);
        Assert.True(store.State.Issues[7].Review!.RequestBoundary!.AlreadyPending);
        Assert.Equal(ReviewLifecycleState.Pending, store.State.Issues[7].Review!.LifecycleState);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        Assert.Equal(DeliveryStatus.Succeeded,
            (await adapter.ReconcileAsync(store.State.Outbox["review"], default)).Status);
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("Copilot")]
    [InlineData("copilot-pull-request-reviewer[bot]")]
    public async Task VerifiedReviewerAliasesAcceptTheSamePinnedBotAcrossRequestAndSubmission(string login)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewerLogin = login };
        using var client = handler.Client();
        var diagnostics = new GitHubReviewRequestDiagnostics();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            Approved, new CandidateValidator(), diagnostics);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal("confirmed", diagnostics.Snapshot!.Outcome);
        Assert.Equal(201, diagnostics.Snapshot.HttpStatus);
        var boundary = store.State.Issues[7].Review!.RequestBoundary!;
        Assert.Equal(175728472, boundary.ReviewerId);
        Assert.Equal("BOT_kgDOCnlnWA", boundary.ReviewerNodeId);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;

        Assert.NotNull(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task CreatedAcknowledgementRecoversWithoutAnotherPost()
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);

        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.True(store.State.Issues[7].Review!.RequestBoundary!.Confirmed);
        Assert.Equal("RRR_kwDOAA", store.State.Issues[7].Review!.CopilotRequestNodeId);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        var recovered = await adapter.ReconcileAsync(store.State.Outbox["review"], default);
        Assert.Equal(outcome, recovered);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", recovered, Now);
        Assert.IsType<ReviewPending>((await adapter.ReadEventAsync(store.State, store.State.Issues[7], default))!.Data);
        handler.HasOutput = true;

        var message = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);

        Assert.Equal(ReviewClassification.NeedsHuman, Assert.IsType<ReviewResult>(message!.Data).Classification);
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task LegacyConfirmedReviewWithoutDeadlineIsNotPromotedToPending()
    {
        var state = Eligible();
        state.Issues[7].Review!.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now,
            [], Confirmed: true, RequestNodeId: "RRR_kwDOAA", AcceptedAt: Now);
        state = new OrchestrationReducer().RecordOutcome(state, "review",
            new(DeliveryStatus.Succeeded, "review-request:review"), Now);
        var store = new Store(state);
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var review = store.State.Issues[7].Review!;
        Assert.Equal(ReviewLifecycleState.NotRequested, review.LifecycleState);
        Assert.Null(review.ReviewDeadlineAt);
        Assert.Null(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task LegacyConfirmedReviewWithoutDeadlineRecoversFromUnknownAndAcceptsResult()
    {
        var state = Eligible();
        state.Issues[7].Review!.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now,
            [], Confirmed: true, RequestNodeId: "RRR_kwDOAA", AcceptedAt: Now);
        state = new OrchestrationReducer().RecordOutcome(state, "review", new(DeliveryStatus.Unknown), Now);
        Assert.Equal(ReviewLifecycleState.RequestUnknown, state.Issues[7].Review!.LifecycleState);
        var store = new Store(state);
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var recovered = await adapter.ReconcileAsync(store.State.Outbox["review"], default);
        Assert.Equal(DeliveryStatus.Succeeded, recovered.Status);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", recovered, Now);
        Assert.Equal(ReviewLifecycleState.NotRequested, store.State.Issues[7].Review!.LifecycleState);
        Assert.Null(store.State.Issues[7].Review!.ReviewDeadlineAt);
        handler.HasOutput = true;

        var message = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.IsType<ReviewResult>(message!.Data);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State, message);

        Assert.Equal(ReviewLifecycleState.Completed, store.State.Issues[7].Review!.LifecycleState);
        Assert.Equal(0, handler.Posts);
    }

    [Theory]
    [InlineData("ack-malformed")]
    [InlineData("ack-state")]
    [InlineData("body-io")]
    [InlineData("lost-response")]
    public async Task InvalidOrLostCreatedAcknowledgementRemainsUnknownAndIsNotRetried(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        await Assert.ThrowsAnyAsync<Exception>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
        Assert.False(store.State.Issues[7].Review!.RequestBoundary!.Confirmed);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        handler.HasOutput = true;

        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.State.Outbox["review"], default)).Status);
        Assert.Null(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("approval", DeliveryStatus.Failed, EffectFailureCode.ReviewPrerequisitesUnverified, 0)]
    [InlineData("bot-id", DeliveryStatus.Failed, EffectFailureCode.ReviewIdentityMismatch, 0)]
    [InlineData("closed", DeliveryStatus.Failed, EffectFailureCode.ReviewPreflightRejected, 0)]
    [InlineData("lost-response", DeliveryStatus.Unknown, EffectFailureCode.ExecutionException, 1)]
    public async Task WorkerPersistsSafeFailureCodesWithoutRetryingReview(string corruption,
        DeliveryStatus status, EffectFailureCode code, int posts)
    {
        var store = new Store(Eligible());
        store.State.Outbox["review"].Status = DeliveryStatus.Pending;
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            corruption == "approval" ? new(true, false, true, true) : Approved, new CandidateValidator());
        var worker = new OrchestrationWorker(new(), new Clock());
        var options = new WorkerOptions { MaximumIterations = 3, PollInterval = TimeSpan.Zero };

        await worker.RunAsync(store, new Events(adapter, false), new Observations(), new Effects(adapter),
            new(false, true, true, true), options);

        var effect = store.State.Outbox["review"];
        Assert.Equal(status, effect.Status);
        Assert.Equal(code, effect.FailureCode);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(posts, handler.Posts);
        if (posts == 0) Assert.Null(store.State.Issues[7].Review!.RequestBoundary);
        else Assert.Equal(7, store.State.AdmittedIssue);

        await worker.RunAsync(store, new Events(adapter, false), new Observations(), new Effects(adapter),
            new(false, true, true, true), options);
        Assert.Equal(posts, handler.Posts);
        Assert.Equal(code, store.State.Outbox["review"].FailureCode);
        Assert.DoesNotContain("Injected response loss", StateJson.Serialize(store.State));
    }

    [Fact]
    public void PreflightRefusalCannotOverwriteAConcurrentPreparedRequestAsDefinitelyFailed()
    {
        var state = Eligible();
        state.Issues[7].Review!.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now, []);
        var next = new OrchestrationReducer().RecordOutcome(state, "review",
            new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPreflightRejected), Now);

        Assert.Equal(DeliveryStatus.Unknown, next.Outbox["review"].Status);
        Assert.Equal(7, next.AdmittedIssue);
        Assert.True(next.Issues[7].ReactivationRequired);
    }

    [Fact]
    public async Task HistoricalUnknownReviewWithoutBoundaryStaysUnknownAndIsNeverRetried()
    {
        var store = new Store(Eligible());
        store.State.Outbox["review"].Status = DeliveryStatus.Unknown;
        store.State.Issues[7].Phase = Phase.BLOCKED;
        store.State.Issues[7].ReactivationRequired = true;
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);

        var outcome = await adapter.ReconcileAsync(store.State.Outbox["review"], default);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);

        Assert.Equal(DeliveryStatus.Unknown, outcome.Status);
        Assert.Null(store.State.Outbox["review"].FailureCode);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(0, handler.Posts);
        var refused = new OrchestrationReducer().RecordOutcome(store.State, "review",
            new(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPrerequisitesUnverified), Now);
        Assert.Equal(DeliveryStatus.Unknown, refused.Outbox["review"].Status);
        Assert.Equal(7, refused.AdmittedIssue);
    }

    [Fact]
    public async Task ConcurrentAdapterExecutorsCannotRepeatNonIdempotentRequest()
    {
        var store = new Store(Eligible());
        var handler = new Api { ConcurrentPreflight = true };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        async Task<bool> Execute()
        {
            try { await adapter.ExecuteAsync(store.State.Outbox["review"], default); return true; }
            catch (InvalidDataException) { return false; }
        }
        var results = await Task.WhenAll(Execute(), Execute());
        Assert.Single(results, success => success);
        Assert.Equal(1, handler.Posts);
        Assert.True(store.State.Issues[7].Review!.RequestBoundary!.Confirmed);
    }

    [Theory]
    [InlineData(DeliveryStatus.Succeeded)]
    [InlineData(DeliveryStatus.Unknown)]
    [InlineData(DeliveryStatus.Executing)]
    public async Task EarlierRequestForSamePullAndCommitPreventsAnotherPost(DeliveryStatus priorStatus)
    {
        var state = Eligible();
        state.Outbox["previous"] = new()
        {
            Id = "previous", IssueNumber = 7, Generation = 1, Kind = EffectKind.RequestReview,
            Sha = Sha, Status = priorStatus
        };
        var handler = new Api();
        using var client = handler.Client();
        var outcome = await CreateAdapter(client, new Store(state)).ExecuteAsync(state.Outbox["review"], default);
        Assert.Equal(DeliveryStatus.Failed, outcome.Status);
        Assert.Equal(EffectFailureCode.ReviewPreflightRejected, outcome.FailureCode);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task FeedbackAcceptedAfterBoundarySavePreventsMutation()
    {
        var store = new Store(Eligible());
        store.AfterSave = () => store.State.Issues[7].PendingFeedbackEventIds.Add("racing-feedback");
        var handler = new Api();
        using var client = handler.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.State.Outbox["review"], default));
        Assert.False(store.State.Issues[7].Review!.RequestBoundary!.Confirmed);
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task ReviewPaginationCapturesNewSubmissionOnSecondPageWithoutReRequest()
    {
        var store = new Store(Eligible());
        var handler = new Api { Paginated = true };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        Assert.Equal(100, store.State.Issues[7].Review!.RequestBoundary!.ExistingReviewIds.Count);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;
        var message = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        Assert.Equal(ReviewClassification.NeedsHuman, Assert.IsType<ReviewResult>(message!.Data).Classification);
        Assert.Equal(1, handler.Posts);
        Assert.Contains(handler.Requests, request => request.Contains("page=2"));
    }

    [Fact]
    public async Task CandidateChangePreventsReviewResultAcceptance()
    {
        var dispatchStore = new Store(Eligible());
        var dispatchHandler = new Api();
        using var dispatchClient = dispatchHandler.Client();
        var dispatchAdapter = new GitHubReviewAdapter(new(dispatchClient, "owner/repo", 1), dispatchStore,
            new Clock(), Approved, new CandidateValidator { Reject = true });
        Assert.Equal(EffectFailureCode.ReviewPreflightRejected,
            (await dispatchAdapter.ExecuteAsync(dispatchStore.State.Outbox["review"], default)).FailureCode);
        Assert.Equal(0, dispatchHandler.Posts);

        var state = Eligible();
        var issue = state.Issues[7];
        issue.Review!.ReviewId = "review-request:review";
        issue.Review.RequestBoundary = new(175728472, "BOT_kgDOCnlnWA", Now, [], Confirmed: true);
        state.Outbox["review"].Status = DeliveryStatus.Succeeded;
        state.Outbox["review"].ExternalId = "review-request:review";
        var store = new Store(state);
        var handler = new Api { HasOutput = true };
        using var client = handler.Client();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(), Approved,
            new CandidateValidator { Reject = true });

        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReadEventAsync(store.State, issue, default));
        Assert.Null(issue.Review.Classification);
        Assert.DoesNotContain(handler.Requests, request => request.EndsWith("/reviews", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteReviewPaginationCannotAuthorizeRequestOrClassification(bool afterRequest)
    {
        var store = new Store(Eligible());
        var handler = new Api { Paginated = true };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        if (afterRequest)
        {
            var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
            store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
            handler.HasOutput = true;
        }
        handler.FailSecondPage = true;
        if (afterRequest)
            await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        else
            Assert.Equal(EffectFailureCode.ReviewPreflightRejected,
                (await adapter.ExecuteAsync(store.State.Outbox["review"], default)).FailureCode);
        Assert.Equal(afterRequest ? 1 : 0, handler.Posts);
        Assert.Null(store.State.Issues[7].Review!.Classification);
        Assert.Equal(Phase.REVIEW_PENDING, store.State.Issues[7].Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveClosureOrHeadChangeStopsPollingButDoesNotPreventReducerReconciliation(bool closed)
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;
        handler.Corruption = closed ? "closed" : "live-sha";
        Assert.Null(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        var reconciled = new OrchestrationReducer().Reconcile(store.State, new()
        {
            ObservedAt = Now, Issues = new() { [7] = new() { Closed = closed, PullRequest = 17,
                HeadSha = closed ? Sha : new string('b', 40), PullRequestMerged = false } }
        }).State;
        Assert.Equal(closed ? Phase.CLOSED : Phase.BLOCKED, reconciled.Issues[7].Phase);
        Assert.Equal(1, handler.Posts);
        Assert.Null(reconciled.Issues[7].Review!.Classification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedWorkerRequestsOnceCapturesReviewAndPreservesQueuedFeedbackAcrossReplay(bool feedback)
    {
        var store = new Store(Eligible());
        store.State.Outbox["review"].Status = DeliveryStatus.Pending;
        var handler = new Api { PublishOutputAfterPost = true };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var worker = new OrchestrationWorker(new(new Allow()), new Clock());
        var result = await worker.RunAsync(store, new Events(adapter, feedback), new Observations(),
            new Effects(adapter), new(false, true, true, true),
            new() { MaximumIterations = 8, PollInterval = TimeSpan.Zero });
        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(ReviewClassification.NeedsHuman, store.State.Issues[7].Review!.Classification);
        Assert.NotNull(store.State.Issues[7].Review!.SourceEvidence);
        if (feedback)
        {
            Assert.Single(store.State.Issues[7].PendingFeedbackEventIds);
            Assert.Equal("human-before-review", Assert.Single(store.State.Issues[7].Feedback.Values).EventId);
        }
        await worker.RunAsync(store, new Events(adapter, feedback), new Observations(), new Effects(adapter),
            new(false, true, true, true), new() { MaximumIterations = 8, PollInterval = TimeSpan.Zero });
        Assert.Equal(1, handler.Posts);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.DoesNotContain(store.State.Issues[7].Tasks.Values, task => task.Status is TaskStatus.Running or TaskStatus.Intended);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task DefaultAndEachMissingReviewApprovalPreventAllIo(bool activation, bool policy, bool cost, bool automatic)
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        var adapter = new GitHubReviewAdapter(new(client, "owner/repo", 1), store, new Clock(),
            new(activation, policy, cost, automatic));
        Assert.Equal(new EffectOutcome(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPrerequisitesUnverified),
            await adapter.ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Equal(new EffectOutcome(DeliveryStatus.Failed, FailureCode: EffectFailureCode.ReviewPrerequisitesUnverified),
            await new GitHubReviewAdapter(new(client, "owner/repo", 1), store)
                .ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("coding")]
    [InlineData("ci")]
    [InlineData("sha")]
    [InlineData("feedback")]
    [InlineData("hold")]
    [InlineData("round")]
    [InlineData("admission")]
    [InlineData("digest")]
    [InlineData("pending-intent")]
    public async Task CanonicalEligibilityGuardsPreventRequest(string corruption)
    {
        var state = Eligible();
        var issue = state.Issues[7];
        var candidate = StateCopy(state).Outbox["review"];
        switch (corruption)
        {
            case "coding": issue.Tasks["task"].Status = TaskStatus.Running; break;
            case "ci": issue.Ci!.Green = false; break;
            case "sha": issue.HeadSha = new string('b', 40); break;
            case "feedback": issue.PendingFeedbackEventIds.Add("human"); break;
            case "hold": issue.ReactivationRequired = true; break;
            case "round": issue.Tasks["task"].Round++; break;
            case "admission": state.AdmittedIssue = null; break;
            case "digest": candidate.Digest = "altered"; break;
            case "pending-intent": state.Outbox["review"].Status = DeliveryStatus.Pending; break;
        }
        var store = new Store(state);
        var handler = new Api();
        using var client = handler.Client();
        if (corruption is "digest" or "pending-intent")
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                CreateAdapter(client, store).ExecuteAsync(candidate, default));
        else
            Assert.Equal(EffectFailureCode.ReviewPreflightRejected,
                (await CreateAdapter(client, store).ExecuteAsync(candidate, default)).FailureCode);
        Assert.Empty(handler.Requests);
        Assert.Null(store.State.Issues[7].Review!.RequestBoundary);
    }

    [Theory]
    [InlineData("live-sha")]
    [InlineData("closed")]
    [InlineData("repository")]
    [InlineData("bot-type")]
    [InlineData("bot-id")]
    [InlineData("bot-node")]
    [InlineData("bot-login")]
    [InlineData("pending-review")]
    public async Task LivePreflightFailuresNeverLaunchReview(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        var outcome = await CreateAdapter(client, store).ExecuteAsync(store.State.Outbox["review"], default);
        Assert.Equal(DeliveryStatus.Failed, outcome.Status);
        Assert.Equal(corruption.StartsWith("bot-", StringComparison.Ordinal)
            ? EffectFailureCode.ReviewIdentityMismatch : EffectFailureCode.ReviewPreflightRejected, outcome.FailureCode);
        Assert.Equal(0, handler.Posts);
        Assert.Null(store.State.Issues[7].Review!.RequestBoundary);
    }

    [Fact]
    public async Task RequestWritesBoundaryBeforePostAndDurableReceiptBeforeOutcome()
    {
        var store = new Store(Eligible());
        var handler = new Api();
        handler.BeforePost = () =>
        {
            Assert.Equal(DeliveryStatus.Executing, store.State.Outbox["review"].Status);
            var boundary = store.State.Issues[7].Review!.RequestBoundary!;
            Assert.False(boundary.Confirmed);
            Assert.Equal(new long[] { 100 }, boundary.ExistingReviewIds);
        };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        Assert.Equal("review-request:review", outcome.ExternalId);
        Assert.True(store.State.Issues[7].Review!.RequestBoundary!.Confirmed);
        Assert.Equal("RRR_kwDOAA", store.State.Issues[7].Review!.CopilotRequestNodeId);
        Assert.Equal(1, handler.Posts);
        Assert.Contains("POST /repos/owner/repo/pulls/17/copilot-code-review", handler.Requests);
        Assert.Equal("{}", handler.RequestBody);
        // Simulate loss of the outer outcome publication; recover the saved acknowledgement without another POST.
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        var recovered = await adapter.ReconcileAsync(store.State.Outbox["review"], default);
        Assert.Equal(outcome, recovered);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", recovered, Now);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("lost-response")]
    [InlineData("ack-state")]
    [InlineData("ack-malformed")]
    [InlineData("body-io")]
    [InlineData("save-confirmation")]
    public async Task AmbiguousPostOrReceiptWriteIsLookupOnlyAndNeverClearsHold(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api { Corruption = corruption };
        if (corruption == "save-confirmation") handler.BeforePost = () => store.FailWrites = true;
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
        store.FailWrites = false;
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", new(DeliveryStatus.Unknown), Now);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.State.Outbox["review"], default)).Status);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.State.Outbox["review"], default)).Status);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Equal(1, handler.Posts);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Equal(1, handler.Posts);
    }

    [Fact]
    public async Task FailureBeforeBoundaryPublicationPerformsNoPost()
    {
        var store = new Store(Eligible()) { FailWrites = true };
        var handler = new Api();
        using var client = handler.Client();
        await Assert.ThrowsAsync<IOException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.State.Outbox["review"], default));
        Assert.Equal(0, handler.Posts);
    }

    [Fact]
    public async Task AbandonmentReadEvidenceRequiresExactLiveHeadAndNoCopilotRequestOrReview()
    {
        var state = UncertainReview();
        var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, new Store(state));
        var evidence = await adapter.ReadAbandonmentEvidenceAsync(state, state.Issues[7], "review", Sha, Now, default);
        Assert.Equal("review", evidence.IntentId);
        Assert.Equal(17, evidence.PullRequest);
        Assert.Equal(Sha, evidence.HeadSha);
        Assert.False(evidence.ReviewerRequestPending);
        Assert.Empty(evidence.ReviewerReviewIds);
        Assert.Equal(0, handler.Posts);
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData("existing-request")]
    [InlineData("abandon-review")]
    [InlineData("live-sha")]
    [InlineData("closed")]
    [InlineData("repository")]
    public async Task AbandonmentEvidenceRefusesPendingReviewHistoryOrChangedPull(string corruption)
    {
        var state = UncertainReview();
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, new Store(state));
        await Assert.ThrowsAnyAsync<Exception>(() =>
            adapter.ReadAbandonmentEvidenceAsync(state, state.Issues[7], "review", Sha, Now, default));
        Assert.Equal(0, handler.Posts);
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData("empty-pending-identity")]
    [InlineData("node-pending-identity")]
    [InlineData("empty-history-identity")]
    [InlineData("node-history-identity")]
    [InlineData("whitespace-pending-identity")]
    [InlineData("whitespace-history-identity")]
    [InlineData("malformed-pending-identity")]
    [InlineData("malformed-history-identity")]
    public async Task AbandonmentRejectsIncompleteOrContradictoryReviewerIdentities(string corruption)
    {
        var state = UncertainReview();
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, new Store(state));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ReadAbandonmentEvidenceAsync(state, state.Issues[7], "review", Sha, Now, default));
        Assert.Equal(0, handler.Posts);
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData("COMMENTED")]
    [InlineData("APPROVED")]
    [InlineData("CHANGES_REQUESTED")]
    [InlineData("DISMISSED")]
    public async Task SubmittedReviewIsDurableNeedsHumanEvidenceNeverGuessedAcceptanceOrBlockers(string reviewState)
    {
        var store = new Store(Eligible());
        var handler = new Api { ReviewState = reviewState };
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;
        var message = await adapter.ReadEventAsync(store.State, store.State.Issues[7], default);
        var result = Assert.IsType<ReviewResult>(message!.Data);
        Assert.Equal(ReviewClassification.NeedsHuman, result.Classification);
        Assert.Empty(result.Findings); // Suggestions are not automatically blockers.
        Assert.Contains("precise comment evidence", result.SourceEvidence);
        Assert.Contains(reviewState, result.SourceEvidence);
        Assert.Equal("review-request:review", result.ReviewId); // Local request receipt, not API review id 101.
        var reducer = new OrchestrationReducer(new Allow());
        store.State = reducer.Apply(store.State, message);
        var applied = StateJson.Serialize(store.State);
        store.State = reducer.Apply(store.State, message);
        Assert.Equal(applied, StateJson.Serialize(store.State));
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.Equal("Review requires human classification.", store.State.Issues[7].HoldReason);
        Assert.Equal(result.SourceEvidence, store.State.Issues[7].Review!.SourceEvidence);
        Assert.NotNull(store.State.Issues[7].CurrentHandoffId);
        Assert.Null(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("old-review")]
    [InlineData("stale-review")]
    [InlineData("other-actor")]
    [InlineData("PENDING")]
    public async Task HistoricalStaleOtherActorAndPendingReviewsDoNotSatisfyResult(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;
        handler.Corruption = corruption;
        Assert.IsType<ReviewPending>((await adapter.ReadEventAsync(store.State, store.State.Issues[7], default))!.Data);
        Assert.Null(store.State.Issues[7].Review!.Classification);
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData("review-url")]
    [InlineData("comment-review")]
    [InlineData("comment-actor")]
    [InlineData("comment-url")]
    public async Task MalformedResultIdentityFailsWithoutPromotingOrRequestingAgain(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api();
        using var client = handler.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.State.Outbox["review"], default);
        store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
        handler.HasOutput = true;
        handler.Corruption = corruption;
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
        Assert.Null(store.State.Issues[7].Review!.Classification);
        Assert.Equal(1, handler.Posts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HostAbandonsUnknownReviewWithFreshEvidenceWithoutLaunchingOrAdmittingWork(bool activation)
    {
        var store = new Store(UncertainReview());
        store.State.Issues[8] = new() { Number = 8, Ready = true, Phase = Phase.READY, ReadyAt = Now };
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();

        var result = await RunAbandonment(client, store, activation: activation);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(0, result.Iterations);
        Assert.Equal(DeliveryStatus.Failed, store.State.Outbox["review"].Status);
        Assert.Equal(EffectFailureCode.OperatorAbandonedUnknownReview, store.State.Outbox["review"].FailureCode);
        var issue = store.State.Issues[7];
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.True(issue.ReactivationRequired);
        Assert.False(issue.ActiveAuthorized);
        Assert.Null(store.State.AdmittedIssue);
        var resolution = Assert.IsType<ReviewOperatorResolution>(issue.Review!.OperatorResolution);
        Assert.Equal(Now, resolution.Evidence.ObservedAt);
        Assert.Equal("review", resolution.Evidence.IntentId);
        Assert.Equal(55, resolution.Author.Id);
        Assert.Empty(store.State.Issues[8].Tasks);
        Assert.Single(issue.Tasks);
        Assert.Contains(handler.Requests, request => request.Contains("/tasks/remote-task"));
        Assert.Contains(handler.Requests, request => request.Contains("/reviews?"));
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task SerializedRecoveryWaitsForExecutorPausedImmediatelyBeforeReviewPost()
    {
        var store = new Store(Eligible());
        var atPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Api
        {
            Corruption = "no-reviews",
            BeforePostAsync = async token =>
            {
                atPost.TrySetResult();
                await releasePost.Task.WaitAsync(token);
            }
        };
        using var client = handler.Client();
        using var slot = new SemaphoreSlim(1);
        // Model the shared, non-cancelling Actions concurrency slot.
        async Task Execute()
        {
            await slot.WaitAsync();
            try
            {
                var outcome = await CreateAdapter(client, store).ExecuteAsync(store.State.Outbox["review"], default);
                store.State = new OrchestrationReducer().RecordOutcome(store.State, "review", outcome, Now);
            }
            finally { slot.Release(); }
        }
        async Task Recover()
        {
            await slot.WaitAsync();
            try { await RunAbandonment(client, store); }
            finally { slot.Release(); }
        }

        var execution = Execute();
        await atPost.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var recovery = Recover();
        try
        {
            Assert.False(recovery.IsCompleted);
            Assert.Equal(0, handler.Posts);
            Assert.Equal(DeliveryStatus.Executing, store.State.Outbox["review"].Status);
            Assert.Equal(7, store.State.AdmittedIssue);
            Assert.Null(store.State.Issues[7].Review!.OperatorResolution);
        }
        finally { releasePost.TrySetResult(); }
        await execution;
        await Assert.ThrowsAsync<InvalidDataException>(() => recovery);
        Assert.Equal(1, handler.Posts);
        Assert.Equal(DeliveryStatus.Succeeded, store.State.Outbox["review"].Status);
        Assert.Null(store.State.Issues[7].Review!.OperatorResolution);
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("error")]
    [InlineData("overflow")]
    public async Task AbandonmentRejectsPendingReviewerOnLaterPageOrIncompletePagination(string pagination)
    {
        var store = new Store(UncertainReview());
        var original = StateJson.Serialize(store.State);
        using var handler = new Api { Corruption = "no-reviews", RequestedReviewerPagination = pagination };
        using var client = handler.Client();

        await Assert.ThrowsAnyAsync<Exception>(() => RunAbandonment(client, store));

        Assert.Equal(original, StateJson.Serialize(store.State));
        Assert.Contains(handler.Requests, request => request.Contains("requested_reviewers?per_page=100&page=2"));
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task AbandonmentAcceptsEmptyEvidenceOnlyAfterLastRequestedReviewerPage()
    {
        var store = new Store(UncertainReview());
        using var handler = new Api { Corruption = "no-reviews", RequestedReviewerPagination = "empty" };
        using var client = handler.Client();

        await RunAbandonment(client, store);

        Assert.Contains(handler.Requests, request => request.Contains("requested_reviewers?per_page=100&page=2"));
        Assert.Equal(DeliveryStatus.Failed, store.State.Outbox["review"].Status);
        Assert.NotNull(store.State.Issues[7].Review!.OperatorResolution);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HostRestoresCommittedAbandonmentAfterReactivationWithoutChangingNewGeneration(
        bool statusEnabled, bool blocked)
    {
        var store = new Store(UncertainReview());
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        await RunAbandonment(client, store);
        store.State = new OrchestrationReducer(new Allow()).Apply(store.State,
            new("reactivate", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human, 55),
                Now.AddMinutes(1), new Reactivate("Start a separate generation.")));
        Assert.Equal(2, store.State.Issues[7].Generation);
        if (blocked)
        {
            store.State.Issues[7].Phase = Phase.BLOCKED;
            store.State.Issues[7].ReactivationRequired = true;
            store.State.Issues[7].HoldReason = "New generation is blocked.";
        }
        var original = StateJson.Serialize(store.State);
        handler.Requests.Clear();
        handler.Corruption = "existing-request";

        var result = await RunAbandonment(client, store, statusEnabled: statusEnabled);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(original, StateJson.Serialize(store.State));
        Assert.DoesNotContain(handler.Requests, request => request.Contains("/tasks/") || request.Contains("/pulls/"));
        Assert.DoesNotContain(handler.Requests, request => request.Contains("/statuses"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RunAbandonment(client, store, reason: "Changed operator decision", statusEnabled: statusEnabled));
        Assert.Equal(original, StateJson.Serialize(store.State));
    }

    [Fact]
    public async Task SameGenerationAbandonmentReplayStillAttemptsRequiredStatusRevocation()
    {
        var store = new Store(UncertainReview());
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        await RunAbandonment(client, store);
        handler.Requests.Clear();

        var result = await RunAbandonment(client, store, statusEnabled: true);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        var status = Assert.Single(store.State.Outbox.Values, item => item.Kind == EffectKind.PublishRequiredStatus);
        Assert.Equal("error", status.RequiredStatus!.State);
        Assert.Equal(DeliveryStatus.Succeeded, status.Status);
        Assert.Equal(1, handler.StatusPosts);
        Assert.Contains(handler.Requests, request => request.Contains("/users/status-publisher"));
        await RunAbandonment(client, store, statusEnabled: true);
        Assert.Equal(1, handler.StatusPosts);
    }

    [Fact]
    public async Task HostRestoresCommittedAbandonmentOnReplayWithoutRepeatingEvidenceReads()
    {
        var store = new Store(UncertainReview());
        store.AfterSave = () =>
        {
            store.AfterSave = null;
            throw new IOException("Lost response after committed abandonment.");
        };
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        await Assert.ThrowsAsync<IOException>(() => RunAbandonment(client, store));
        var persisted = StateJson.Serialize(store.State);
        handler.Requests.Clear();
        handler.Corruption = "existing-request";

        var result = await RunAbandonment(client, store);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(persisted, StateJson.Serialize(store.State));
        Assert.DoesNotContain(handler.Requests, request =>
            request.Contains("/tasks/") || request.Contains("/pulls/"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RunAbandonment(client, store, reason: "Changed operator decision"));
        Assert.Equal(persisted, StateJson.Serialize(store.State));
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData("existing-request")]
    [InlineData("abandon-review")]
    [InlineData("live-sha")]
    [InlineData("read-permission")]
    [InlineData("definition-sha")]
    public async Task HostRejectsUnsafeAbandonmentWithoutCanonicalMutation(string corruption)
    {
        var store = new Store(UncertainReview());
        var original = StateJson.Serialize(store.State);
        using var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        await Assert.ThrowsAnyAsync<Exception>(() => RunAbandonment(client, store));
        Assert.Equal(original, StateJson.Serialize(store.State));
        Assert.All(handler.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RecoveryApprovalCannotEnableNormalHostExecutionOrBypassMissingRecoveryApproval(
        bool commandOnly, bool missingApproval)
    {
        var store = new Store(UncertainReview());
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunAbandonment(client, store, commandOnly: commandOnly, missingApproval: missingApproval));
        Assert.Equal(DeliveryStatus.Unknown, store.State.Outbox["review"].Status);
        Assert.Null(store.State.Issues[7].Review!.OperatorResolution);
        Assert.DoesNotContain(handler.Requests, request => request.Contains("/tasks/"));
    }

    [Fact]
    public async Task RecoveryOnlyHostRejectsReactivationEvenWithVerifiedRecoveryApproval()
    {
        var store = new Store(UncertainReview());
        using var handler = new Api { Corruption = "no-reviews" };
        using var client = handler.Client();
        var original = StateJson.Serialize(store.State);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            RunAbandonment(client, store, operation: "reactivate"));
        Assert.Equal(original, StateJson.Serialize(store.State));
    }

    private static async Task<WorkerResult> RunAbandonment(HttpClient client, Store store, bool activation = false,
        string reason = "Accept possible prior delivery", bool commandOnly = true, bool missingApproval = false,
        string operation = "abandon-uncertain-review", bool statusEnabled = false)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation, reason, review_intent = "review",
                pull = "17", sha = Sha, acknowledge_possible_prior_submission = "true" }
        });
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var occurredAt = await GitHubLiveCommand.VerifyCommandDefinitionAsync(api,
            new(10, Sha), "44", "workflow_dispatch");
        return await new GitHubOrchestrationHost(api, store, new Clock()).ReconcileAsync(payload,
            "workflow_dispatch", "44", 1, 1, occurredAt, [], 99, "master", 55,
            new(activation, true, true, true), commandOnly: commandOnly,
            statusPublisher: statusEnabled ? new(99, "status-publisher", "publisher-node", "Bot") : null,
            statusApproval: statusEnabled ? new(true, true, true, true, true) : null,
            recoveryApproval: missingApproval ? null : new(true, true, true));
    }

    private static RepositoryState Eligible()
    {
        // Independent completed-task/exact-SHA CI fixture, not provider proof or live orchestration.
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        state.Issues[7] = new IssueState
        {
            Number = 7, Phase = Phase.REVIEW_PENDING, ActiveAuthorized = true, PullRequest = 17, HeadSha = Sha,
            ActiveTaskId = "task", Round = 1,
            Tasks = new() { ["task"] = new() { Id = "task", Status = TaskStatus.Completed, LaunchConfirmed = true,
                ExternalId = "remote-task", FinalSha = Sha, Round = 1,
                CandidateShaEvidence = new(TaskTerminalRecovery.CandidateShaEvidenceVersion,
                    TaskTerminalRecovery.CandidateShaPolicy, "task", "remote-task", ["session"], 17, Sha) } },
            Ci = new() { IntentId = "ci", TaskId = "task", Sha = Sha, Round = 1, Green = true, RunId = "run" },
            Review = new() { IntentId = "review", TaskId = "task", Sha = Sha, Round = 1 }
        };
        state.Outbox["ci"] = new() { Id = "ci", IssueNumber = 7, Kind = EffectKind.RunCi,
            Status = DeliveryStatus.Succeeded, ExternalId = "run", Sha = Sha, TaskId = "task", Round = 1, Generation = 1 };
        state.Outbox["review"] = new() { Id = "review", Digest = "fixture", IssueNumber = 7,
            Kind = EffectKind.RequestReview, Status = DeliveryStatus.Executing, Sha = Sha,
            TaskId = "task", Round = 1, Generation = 1 };
        return state;
    }

    private static RepositoryState UncertainReview()
    {
        var state = Eligible();
        var issue = state.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.ActiveAuthorized = false;
        issue.ReactivationRequired = true;
        issue.HoldReason = "Effect outcome is ambiguous.";
        state.Outbox["review"].Status = DeliveryStatus.Unknown;
        state.Outbox["launch"] = new() { Id = "launch", IssueNumber = 7, Generation = 1,
            TaskId = "task", Round = 1, Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Succeeded,
            ExternalId = "remote-task" };
        return state;
    }

    private static RepositoryState StateCopy(RepositoryState state) => StateJson.Deserialize(StateJson.Serialize(state));
    private sealed class Events(GitHubReviewAdapter adapter, bool feedback) : IWorkerEventSource
    {
        public async Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token)
        {
            var result = await adapter.ReadEventAsync(state, state.Issues[7], token);
            var messages = new List<OrchestrationEvent>();
            if (feedback && result is not null && !state.Inbox.ContainsKey("human-before-review"))
                messages.Add(new("human-before-review", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human),
                    Now, new HumanFeedback("Consider human feedback first.")));
            if (result is not null && !state.Inbox.ContainsKey(result.Id)) messages.Add(result);
            return new(messages);
        }
    }
    private sealed class Observations : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) =>
            Task.FromResult(new LiveObservation
            {
                ObservedAt = Now, Issues = new() { [7] = new() { PullRequest = 17, HeadSha = Sha,
                    Labels = OrchestrationReducer.ProjectLabels(state.Issues[7]).ToList() } }
            });
    }
    private sealed class Effects(GitHubReviewAdapter adapter) : IVerifiedAgentEffectAdapter
    {
        public bool AgentEffectsAuthorized => true; // Offline composition; no task or CI adapters.
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) =>
            intent.Kind == EffectKind.RequestReview ? adapter.ExecuteAsync(intent, token) :
            intent.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus
                ? Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "offline-projection"))
                : throw new InvalidOperationException("No additional coding/CI effects allowed.");
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) => adapter.ReconcileAsync(intent, token);
    }
    private sealed class CandidateValidator : ICandidateShaValidator
    {
        public bool Reject { get; init; }

        public Task ValidateCandidateShaAsync(RepositoryState state, IssueState issue, CancellationToken token)
        {
            if (Reject) throw new InvalidDataException("Candidate is no longer current.");
            var task = issue.Tasks[issue.ActiveTaskId!];
            TaskTerminalRecovery.ValidateCandidateSha(task.CandidateShaEvidence, task, issue.PullRequest!.Value, task.FinalSha!);
            return Task.CompletedTask;
        }
    }
    private sealed class Allow : IAuthorizationPolicy { public bool IsAuthorized(OrchestrationEvent message) => true; }
    private sealed class Clock(DateTimeOffset? instant = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant ?? Now;
    }
    private sealed class Store(RepositoryState state) : IStateStore
    {
        private readonly object gate = new();
        public RepositoryState State { get; set; } = state;
        public bool FailWrites { get; set; }
        public Action? AfterSave { get; set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token)
        {
            lock (gate) return Task.FromResult(new StateSnapshot(State.Version.ToString(), StateCopy(State)));
        }
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            lock (gate)
            {
                if (FailWrites) throw new IOException("Injected review persistence failure");
                if (State.Version.ToString() != expected.Revision) return Task.FromResult(false);
                State = StateCopy(next);
                AfterSave?.Invoke();
                return Task.FromResult(true);
            }
        }
    }

    private sealed class Api : HttpMessageHandler
    {
        public string? Corruption { get; set; }
        public string ReviewState { get; set; } = "COMMENTED";
        public HttpStatusCode ReviewPostStatus { get; init; } = HttpStatusCode.Created;
        public IReadOnlyList<HttpStatusCode> ReviewPostStatuses { get; init; } = [];
        public string? ApiFailureReason { get; init; }
        public int? LoseReviewPostResponseOn { get; init; }
        public string RequestNodeId { get; init; } = "RRR_kwDOAA";
        public bool HasOutput { get; set; }
        public bool PublishOutputAfterPost { get; init; }
        public bool Paginated { get; init; }
        public bool FailSecondPage { get; set; }
        public bool ConcurrentPreflight { get; init; }
        public string? RequestedReviewerPagination { get; init; }
        public Func<CancellationToken, Task>? BeforePostAsync { get; init; }
        private readonly TaskCompletionSource bothPreflights = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int preflightReads;
        public int Posts { get; private set; }
        public int StatusPosts { get; private set; }
        private object? createdStatus;
        public List<string> Requests { get; } = [];
        public string RequestBody { get; private set; } = "";
        public Action? BeforePost { get; set; }
        public HttpClient Client() => new(this) { BaseAddress = new("https://api.github.com/") };
        public string ReviewerLogin { get; init; } = "Copilot";
        private object AbandonmentActor => Corruption switch
        {
            "empty-pending-identity" or "empty-history-identity" => new { },
            "node-pending-identity" or "node-history-identity" => new
                { id = 66, node_id = "BOT_kgDOCnlnWA", login = "impostor", type = "Bot" },
            "whitespace-pending-identity" or "whitespace-history-identity" => new
                { id = 66, node_id = " \t ", login = "maintainer", type = "User" },
            "malformed-pending-identity" or "malformed-history-identity" => new
                { id = "66", node_id = "USER_node", login = "maintainer", type = "User" },
            _ => Actor()
        };
        private object Actor(bool bad = false) => new
        {
            id = bad || Corruption == "bot-id" ? 66 : 175728472,
            node_id = Corruption == "bot-node" ? "wrong-node" : "BOT_kgDOCnlnWA",
            login = Corruption == "bot-login" ? "impostor" : ReviewerLogin,
            type = Corruption == "bot-type" ? "User" : "Bot"
        };
        private object Pull() => new
        {
            number = 17, state = Corruption == "closed" ? "closed" : "open",
            head = new { @ref = "agent/task", sha = Corruption == "live-sha" ? new string('b', 40) : Sha,
                repo = new { id = Corruption == "repository" ? 2 : 1 } },
            @base = new { @ref = "master", repo = new { id = 1 } }
        };
        private object Review(bool baseline) => new
        {
            id = baseline || Corruption == "old-review" ? 100 : 101, node_id = "REVIEW_node",
            user = Actor(Corruption == "other-actor"), body = "Review body preserved without semantic inference",
            state = Corruption is "pending-review" or "PENDING" ? "PENDING" : ReviewState,
            commit_id = Corruption == "stale-review" ? new string('b', 40) : Sha,
            submitted_at = baseline ? Now.AddDays(-1) : Now.AddSeconds(1),
            pull_request_url = Corruption == "review-url" ? "https://api.github.com/repos/other/repo/pulls/17" : PullUrl
        };
        private const string PullUrl = "https://api.github.com/repos/owner/repo/pulls/17";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            object response;
            if (request.Method == HttpMethod.Post && path == $"/repos/owner/repo/statuses/{Sha}")
            {
                StatusPosts++;
                using var content = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var body = content.RootElement;
                response = createdStatus = new
                {
                    id = 100, url = "https://api.github.com/repos/owner/repo/statuses/100",
                    creator = new { id = 99, login = "status-publisher", node_id = "publisher-node", type = "Bot" },
                    state = body.GetProperty("state").GetString(), context = body.GetProperty("context").GetString(),
                    description = body.GetProperty("description").GetString(),
                    target_url = body.GetProperty("target_url").GetString(), created_at = Now
                };
            }
            else if (request.Method == HttpMethod.Post && path == "/repos/owner/repo/pulls/17/copilot-code-review")
            {
                BeforePost?.Invoke();
                if (BeforePostAsync is not null) await BeforePostAsync(token);
                Posts++;
                var postedStatus = ReviewPostStatuses.Count >= Posts
                    ? ReviewPostStatuses[Posts - 1] : ReviewPostStatus;
                RequestBody = await request.Content!.ReadAsStringAsync(token);
                if (Corruption == "lost-response" || Posts == LoseReviewPostResponseOn)
                    throw new HttpRequestException("Injected response loss");
                if (PublishOutputAfterPost) HasOutput = true;
                response = Corruption switch
                {
                    "ack-malformed" => new { node_id = "", state = "PENDING" },
                    "ack-state" => new { node_id = RequestNodeId, state = "COMPLETED" },
                    _ when postedStatus != HttpStatusCode.Created =>
                        new { message = "Copilot review request rejected.", failure_reason = ApiFailureReason },
                    _ => new { node_id = RequestNodeId, state = "PENDING" }
                };
            }
            else if (path == "/repos/owner/repo") response = new { id = 1, full_name = "owner/repo" };
            else if (path == "/repos/owner/repo/issues/7") response = new { id = 70, number = 7, state = "open",
                labels = new[] { new { name = "agent:blocked" } } };
            else if (path == "/repos/owner/repo/actions/runs/44") response = new
            {
                id = 44, workflow_id = 10, path = ".github/workflows/agent-state-machine.yml",
                @event = "workflow_dispatch", actor = new { id = 55 }, repository = new { id = 1 },
                head_sha = Corruption == "definition-sha" ? new string('b', 40) : Sha,
                created_at = Now.AddDays(-1)
            };
            else if (path == "/users/maintainer") response = new { id = 55, type = "User" };
            else if (path == "/repos/owner/repo/collaborators/maintainer/permission")
                response = new { permission = Corruption == "read-permission" ? "read" : "write", user = new { id = 55 } };
            else if (path == "/agents/repos/owner/repo/tasks/remote-task") response = new
            {
                id = "remote-task", repository = new { id = 1 }, creator = new { id = 55 },
                state = "completed", session_count = 1,
                sessions = new[] { new { id = "session", state = "completed", task_id = "remote-task",
                    repository = new { id = 1 } } },
                artifacts = new[] { new { provider = "github", type = "branch",
                    data = new { head_ref = "agent/task", base_ref = "master" } } }
            };
            else if (path == "/repos/owner/repo/pulls") response = new[] { new { number = 17 } };
            else if (path == "/repos/owner/repo/pulls/17") response = Pull();
            else if (path == "/users/status-publisher")
                response = new { id = 99, login = "status-publisher", node_id = "publisher-node", type = "Bot" };
            else if (path == $"/repos/owner/repo/commits/{Sha}/status")
                response = new { sha = Sha, repository = new { id = 1, full_name = "owner/repo" } };
            else if (path == $"/repos/owner/repo/commits/{Sha}/statuses")
                response = createdStatus is null ? Array.Empty<object>() : new[] { createdStatus };
            else if (path.StartsWith("/users/")) response = Actor();
            else if (path.EndsWith("/requested_reviewers"))
            {
                var paginated = request.RequestUri!.Query.Contains("per_page=100");
                var firstPage = request.RequestUri.Query.EndsWith("&page=1", StringComparison.Ordinal);
                if (paginated && RequestedReviewerPagination is not null)
                {
                    if (!firstPage && RequestedReviewerPagination == "error")
                        throw new HttpRequestException("Injected requested-reviewer page failure");
                    var users = firstPage || RequestedReviewerPagination == "overflow"
                        ? Enumerable.Range(1, 100).Select(id => (object)new
                            { id, node_id = $"USER_{id}", login = $"reviewer-{id}", type = "User" }).ToArray()
                        : RequestedReviewerPagination == "copilot" ? [Actor()] : Array.Empty<object>();
                    response = new { users, teams = Array.Empty<object>() };
                }
                else
                response = new { users =
                    Corruption is "existing-request" or "abandon-pending" ? new object[] { Actor() } :
                    Corruption is "empty-pending-identity" or "node-pending-identity" or
                        "whitespace-pending-identity" or "malformed-pending-identity"
                        ? new object[] { AbandonmentActor } : [], teams = Array.Empty<object>() };
            }
            else if (path.EndsWith("/reviews"))
            {
                if (ConcurrentPreflight)
                {
                    if (Interlocked.Increment(ref preflightReads) == 2) bothPreflights.TrySetResult();
                    await bothPreflights.Task.WaitAsync(token);
                }
                var secondPage = request.RequestUri!.Query.Contains("page=2");
                if (secondPage && FailSecondPage) throw new HttpRequestException("Injected second-page lookup failure");
                if (Paginated && !secondPage)
                    response = Enumerable.Range(1, 100).Select(id => new { id, state = "COMMENTED", user = Actor() }).ToArray();
                else if (Corruption is "empty-history-identity" or "node-history-identity" or
                    "whitespace-history-identity" or "malformed-history-identity")
                    response = new[] { new { id = 101, state = "COMMENTED", user = AbandonmentActor } };
                else response = Corruption == "no-reviews" ? [] : Paginated ? HasOutput ? new[] { Review(false) } : [] :
                    HasOutput ? new[] { Review(true), Review(false) } : new[] { Review(true) };
            }
            else if (path.EndsWith("/comments"))
                response = new[] { new { id = 201, pull_request_review_id = Corruption == "comment-review" ? 999 : 101,
                    user = Actor(Corruption == "comment-actor"), body = "precise comment evidence", path = "source.cs",
                    pull_request_url = Corruption == "comment-url" ? "https://api.github.com/repos/other/repo/pulls/17" : PullUrl } };
            else throw new InvalidOperationException("Unexpected review fixture route");
            var status = request.Method == HttpMethod.Post && path.EndsWith("/copilot-code-review", StringComparison.Ordinal)
                ? ReviewPostStatuses.Count >= Posts ? ReviewPostStatuses[Posts - 1] : ReviewPostStatus
                : HttpStatusCode.OK;
            var result = new HttpResponseMessage(status)
            {
                Content = request.Method == HttpMethod.Post && path.EndsWith("/copilot-code-review", StringComparison.Ordinal) &&
                    Corruption == "body-io"
                    ? new FailingContent()
                    : new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json")
            };
            result.Headers.Add("X-GitHub-Request-Id", "review:request:123");
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
                result.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return result;
        }

        private sealed class FailingContent : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                Task.FromException(new IOException("Injected response-body read failure."));
            protected override Task<Stream> CreateContentReadStreamAsync() =>
                Task.FromException<Stream>(new IOException("Injected response-body read failure."));
        }
    }
}
