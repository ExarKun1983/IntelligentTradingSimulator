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
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            dispatchAdapter.ExecuteAsync(dispatchStore.State.Outbox["review"], default));
        Assert.Equal(0, dispatchHandler.Posts);

        var state = Eligible();
        var issue = state.Issues[7];
        issue.Review!.ReviewId = "review-request:review";
        issue.Review.RequestBoundary = new(55, "BOT_node", Now, [], Confirmed: true);
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
            await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(store.State.Outbox["review"], default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GitHubReviewAdapter(new(client, "owner/repo", 1), store).ExecuteAsync(store.State.Outbox["review"], default));
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
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter(client, store).ExecuteAsync(candidate, default));
        Assert.Empty(handler.Requests);
        Assert.Null(store.State.Issues[7].Review!.RequestBoundary);
    }

    [Theory]
    [InlineData("live-sha")]
    [InlineData("closed")]
    [InlineData("repository")]
    [InlineData("bot-type")]
    [InlineData("existing-request")]
    [InlineData("pending-review")]
    public async Task LivePreflightFailuresNeverLaunchReview(string corruption)
    {
        var store = new Store(Eligible());
        var handler = new Api { Corruption = corruption };
        using var client = handler.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.State.Outbox["review"], default));
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
        Assert.Equal(1, handler.Posts);
        Assert.Contains("copilot-pull-request-reviewer[bot]", handler.RequestBody);
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
    [InlineData("ack-sha")]
    [InlineData("ack-actor")]
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
        Assert.Null(await adapter.ReadEventAsync(store.State, store.State.Issues[7], default));
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
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
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
        public bool HasOutput { get; set; }
        public bool PublishOutputAfterPost { get; init; }
        public bool Paginated { get; init; }
        public bool FailSecondPage { get; set; }
        public bool ConcurrentPreflight { get; init; }
        private readonly TaskCompletionSource bothPreflights = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int preflightReads;
        public int Posts { get; private set; }
        public List<string> Requests { get; } = [];
        public string RequestBody { get; private set; } = "";
        public Action? BeforePost { get; set; }
        public HttpClient Client() => new(this) { BaseAddress = new("https://api.github.com/") };
        private object Actor(bool bad = false) => new { id = bad ? 66 : 55, node_id = "BOT_node",
            login = "copilot-pull-request-reviewer[bot]", type = Corruption == "bot-type" ? "User" : "Bot" };
        private object Pull(bool acknowledgement = false) => new
        {
            number = 17, state = Corruption == "closed" ? "closed" : "open",
            head = new { sha = Corruption == "live-sha" || acknowledgement && Corruption == "ack-sha" ? new string('b', 40) : Sha,
                repo = new { id = Corruption == "repository" ? 2 : 1 } },
            @base = new { repo = new { id = 1 } },
            requested_reviewers = new[] { Actor(acknowledgement && Corruption == "ack-actor") }
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
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/repos/owner/repo/pulls/17/requested_reviewers", path);
                BeforePost?.Invoke();
                Posts++;
                RequestBody = await request.Content!.ReadAsStringAsync(token);
                if (Corruption == "lost-response") throw new HttpRequestException("Injected response loss");
                if (PublishOutputAfterPost) HasOutput = true;
                response = Pull(true);
            }
            else if (path == "/repos/owner/repo") response = new { id = 1, full_name = "owner/repo" };
            else if (path == "/repos/owner/repo/issues/7") response = new { number = 7, state = "open" };
            else if (path == "/repos/owner/repo/pulls/17") response = Pull();
            else if (path.StartsWith("/users/")) response = Actor();
            else if (path.EndsWith("/requested_reviewers"))
                response = new { users = Corruption == "existing-request" ? new[] { Actor() } : [], teams = Array.Empty<object>() };
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
                else response = Paginated ? HasOutput ? new[] { Review(false) } : [] :
                    HasOutput ? new[] { Review(true), Review(false) } : new[] { Review(true) };
            }
            else if (path.EndsWith("/comments"))
                response = new[] { new { id = 201, pull_request_review_id = Corruption == "comment-review" ? 999 : 101,
                    user = Actor(Corruption == "comment-actor"), body = "precise comment evidence", path = "source.cs",
                    pull_request_url = Corruption == "comment-url" ? "https://api.github.com/repos/other/repo/pulls/17" : PullUrl } };
            else throw new InvalidOperationException("Unexpected review fixture route");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
}
