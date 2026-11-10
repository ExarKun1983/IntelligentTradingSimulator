using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubHostIntegrationTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Fact]
    public async Task HostReadsCanonicalGitAndVerifiedApisButNeverPublishesOrLaunches()
    {
        var fixture = new ApiFixture();
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var store = new GitStateStore(new GitHubGitTransport(client, "owner/repo"), "owner/repo");
        var host = new GitHubOrchestrationHost(api, store, new FixedClock());
        var payload = Notification();

        var result = await host.PreviewAsync(payload, "issues", "44", 1, 1, Now, []);
        var duplicate = await host.PreviewAsync(payload, "issues", "44", 1, 1, Now, []);

        Assert.Equal(WorkerStopReason.DryRun, result.Reason);
        var state = result.LastReconciliation!.State;
        Assert.Equal(Phase.CODING, state.Issues[7].Phase);
        Assert.Equal(TaskLaunchSource.UnmappedIssue, Assert.Single(state.Issues[7].Tasks.Values).LaunchSource);
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(StateJson.Serialize(state), StateJson.Serialize(duplicate.LastReconciliation!.State));
        Assert.Contains(result.LastReconciliation.Decisions, effect => effect.Kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
        Assert.Contains(fixture.Requests, request => request.Path.Contains("/git/blobs/"));
        Assert.Empty(fixture.State.Inbox);
    }

    [Fact]
    public async Task ActionsBotProjectionIsIgnoredWithoutRequiringBotCollaboratorPermission()
    {
        var fixture = new ApiFixture { BotNotification = true };
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1),
            new GitStateStore(new GitHubGitTransport(client, "owner/repo"), "owner/repo"), new FixedClock());
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = GitHubLiveCredentials.ProjectionActorId, login = GitHubLiveCredentials.ProjectionLogin, type = "Bot" },
            issue = new { id = 70, number = 7 }, action = "labeled", label = new { name = "agent:ready" }
        });
        var result = await host.PreviewAsync(payload, "issues", "44", 1, 1, Now,
            [GitHubLiveCredentials.ProjectionActorId]);
        Assert.Empty(result.LastReconciliation!.State.Inbox);
        Assert.Empty(result.LastReconciliation.State.Issues);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.Contains("/collaborators/"));
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("actor")]
    [InlineData("permission-user")]
    [InlineData("permission")]
    [InlineData("run-actor")]
    [InlineData("workflow")]
    [InlineData("issue")]
    public async Task HostRejectsUntrustedOrUnauthorizedApiEvidence(string corruption)
    {
        var fixture = new ApiFixture { Corruption = corruption };
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1),
            new GitStateStore(new GitHubGitTransport(client, "owner/repo"), "owner/repo"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            host.PreviewAsync(Notification(), "issues", "44", 1, 1, Now, []));
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task MissingCanonicalBranchCannotBootstrapFromLabels()
    {
        var fixture = new ApiFixture { MissingState = true };
        using var client = fixture.Client();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1),
            new GitStateStore(new GitHubGitTransport(client, "owner/repo"), "owner/repo"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            host.PreviewAsync(Notification(), "issues", "44", 1, 1, Now, []));
        Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task BoundedCommandRejectsOutOfScopeStateOnLaterDispatchLoad()
    {
        using var fixture = new GitHubTaskIntegrationTests.ApiFixture();
        using var client = fixture.Client();
        var store = new ChangingStore();
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new FixedClock());

        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileAsync(
            fixture.Notification(), "issues", "44", 1, 1, Now, [], 99, "master", 55,
            new(true, true, true, true), boundedScope: new(7, 1)));

        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task BoundedWakeRejectsOutOfScopeEffectSnapshotBeforeVerification()
    {
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState { Number = 7, MaxRepairTasks = 1 };
        state.Outbox["foreign"] = new EffectIntent { Id = "foreign", IssueNumber = 8 };
        var store = new GitHubTaskIntegrationTests.Store(state);
        using var client = new HttpClient(new NoRequests()) { BaseAddress = new("https://api.github.com/") };
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new FixedClock());

        await Assert.ThrowsAsync<InvalidDataException>(() => host.ReconcileWakeAsync(
            JsonDocument.Parse("{}").RootElement, "schedule", "44", new(10, Sha),
            new(true, true, true, true), 99, "master", 55,
            new(true, true, true, true), boundedScope: new(7, 1)));
    }

    [Fact]
    public async Task ObservationReadsMappedPrButDoesNotInferCompletionFromItsHead()
    {
        var fixture = new ApiFixture();
        fixture.State.Issues[7] = new IssueState { Number = 7, PullRequest = 17, HeadSha = Sha };
        using var client = fixture.Client();
        var observation = await new GitHubRepositoryApi(client, "owner/repo", 1)
            .ObserveAsync(fixture.State, Now, default);
        Assert.Equal(17, observation.Issues[7].PullRequest);
        Assert.Equal(Sha, observation.Issues[7].HeadSha);
        Assert.Null(observation.Issues[7].CompletedTaskId);
    }

    [Fact]
    public async Task ObservationExcludesRetiredIssueWhileReadingActiveRetestIssue()
    {
        var fixture = new ApiFixture();
        fixture.State.Issues[227] = new IssueState { Number = 227 };
        fixture.State.Issues[237] = new IssueState { Number = 237 };
        using var client = fixture.Client();

        var observation = await new GitHubRepositoryApi(client, "owner/repo", 1)
            .ObserveAsync(fixture.State, Now, default, excludedIssueNumber: 227);

        Assert.DoesNotContain(227, observation.Issues.Keys);
        Assert.Contains(237, observation.Issues.Keys);
        Assert.DoesNotContain(fixture.Requests, request => request.Path.EndsWith("/issues/227", StringComparison.Ordinal));
        Assert.Contains(fixture.Requests, request => request.Path.EndsWith("/issues/237", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusIntentIsStableAcrossReconcileAndRedactsArbitraryFeedbackAndHoldText()
    {
        var fixture = new ApiFixture();
        var issue = new IssueState
        {
            Number = 7, Phase = Phase.BLOCKED, ReactivationRequired = true,
            HoldReason = "untrusted secret text", LastFeedbackText = "untrusted private comment"
        };
        fixture.State.Issues[7] = issue;
        using var client = fixture.Client();
        var observation = await new GitHubRepositoryApi(client, "owner/repo", 1).ObserveAsync(fixture.State, Now, default);
        var reducer = new OrchestrationReducer();
        var first = reducer.Reconcile(fixture.State, observation).State;
        var second = reducer.Reconcile(first, observation).State;
        Assert.Single(second.Outbox.Values, effect => effect.Kind == EffectKind.ProjectStatus);
        var intent = second.Outbox.Values.Single(effect => effect.Kind == EffectKind.ProjectStatus);
        Assert.DoesNotContain("untrusted", intent.StatusBody!);
        Assert.Contains("BLOCKED", intent.StatusBody!);
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        var result = await adapter.ExecuteAsync(intent, default);
        Assert.Equal(DeliveryStatus.Succeeded, result.Status);
        Assert.Equal("101", result.ExternalId);
        var recovered = await adapter.ReconcileAsync(intent, default);
        Assert.Equal(result, recovered);
        Assert.Single(fixture.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task ReclosingSameGenerationRequeuesOldStatusAndUpdatesTheSingleOwnedComment()
    {
        var fixture = new ApiFixture { Closed = true };
        fixture.State.Issues[7] = new IssueState { Number = 7 };
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var adapter = new GitHubProjectionAdapter(api, 99, true);
        var reducer = new OrchestrationReducer();
        var state = fixture.State;
        string? closedIntent = null;
        foreach (var closed in new[] { true, false, true })
        {
            fixture.Closed = closed;
            state = reducer.Reconcile(state, await api.ObserveAsync(state, Now, default)).State;
            var intent = state.Outbox.Values.Single(effect =>
                effect.Kind == EffectKind.ProjectStatus && effect.Status == DeliveryStatus.Pending);
            if (closed)
            {
                if (closedIntent is not null) Assert.Equal(closedIntent, intent.Id);
                closedIntent = intent.Id;
            }
            state = reducer.MarkExecuting(state, intent.Id, Now);
            var outcome = await adapter.ExecuteAsync(state.Outbox[intent.Id], default);
            state = reducer.RecordOutcome(state, intent.Id, outcome, Now);
        }
        Assert.Single(fixture.Comments);
        Assert.Contains("**CLOSED**", fixture.Comments[0].Body);
        Assert.Single(fixture.Requests, request => request.Method == "POST");
        Assert.Equal(2, fixture.Requests.Count(request => request.Method == "PATCH"));
        state = reducer.Reconcile(state, await api.ObserveAsync(state, Now, default)).State;
        Assert.DoesNotContain(state.Outbox.Values,
            effect => effect.Kind == EffectKind.ProjectStatus && effect.Status == DeliveryStatus.Pending);
    }

    [Fact]
    public async Task WithdrawnUnadmittedReadyIssueCancelsUnexecutableStatusAndWorkerSettles()
    {
        var fixture = new ApiFixture();
        fixture.Labels.Clear();
        fixture.State.Issues[7] = new IssueState { Number = 7, Ready = false, Phase = Phase.READY };
        fixture.State.Outbox["old-queued-status"] = new EffectIntent
        {
            Id = "old-queued-status", Kind = EffectKind.ProjectStatus, IssueNumber = 7, Generation = 1,
            StatusBody = StatusProjection.Render(fixture.State.Issues[7]), Status = DeliveryStatus.Pending
        };
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var store = new Store(fixture.State);
        var result = await new OrchestrationWorker().RunAsync(store, new EmptyEvents(), new Observations(api),
            new GitHubProjectionAdapter(api, 99, true), new ReconcileOptions(false, true, true, true),
            new WorkerOptions { MaximumIterations = 2 });
        Assert.Equal(WorkerStopReason.Settled, result.Reason);
        Assert.Null(store.State.AdmittedIssue);
        Assert.Equal(DeliveryStatus.Cancelled, store.State.Outbox["old-queued-status"].Status);
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task WorkerPersistsLostCommentOutcomeThenRecoversLookupOnlyWithoutClearingHold()
    {
        var fixture = new ApiFixture { LoseWriteResponse = true };
        fixture.Labels.Clear();
        fixture.Labels.Add("agent:blocked");
        fixture.State.Issues[7] = new IssueState { Number = 7, Phase = Phase.BLOCKED, ReactivationRequired = true };
        using var client = fixture.Client();
        var api = new GitHubRepositoryApi(client, "owner/repo", 1);
        var store = new Store(fixture.State);
        var effects = new GitHubProjectionAdapter(api, 99, true);
        var options = new ReconcileOptions(false, true, true, true);
        var worker = new OrchestrationWorker();
        var first = await worker.RunAsync(store, new EmptyEvents(), new Observations(api), effects, options);
        Assert.Equal(WorkerStopReason.Blocked, first.Reason);
        Assert.Single(store.State.Outbox.Values, effect => effect.Status == DeliveryStatus.Unknown);
        Assert.Equal(7, store.State.AdmittedIssue);

        await worker.RunAsync(store, new EmptyEvents(), new Observations(api), effects, options,
            new WorkerOptions { MaximumIterations = 1 });
        Assert.DoesNotContain(store.State.Outbox.Values, effect => effect.Status == DeliveryStatus.Unknown);
        Assert.Equal(Phase.BLOCKED, store.State.Issues[7].Phase);
        Assert.True(store.State.Issues[7].ReactivationRequired);
        Assert.Single(fixture.Requests, request => request.Method == "POST");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncompleteOrOversizedCommentCollectionsFailClosed(bool paginated)
    {
        var fixture = new ApiFixture { RepeatFullCommentPage = paginated, OversizedComments = !paginated };
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReconcileAsync(StatusIntent(), default));
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
        Assert.InRange(fixture.Requests.Count, 2, 22);
    }

    [Fact]
    public async Task LostStatusCreateResponseRecoversByLookupWithoutSecondCreate()
    {
        var fixture = new ApiFixture { LoseWriteResponse = true };
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        var intent = StatusIntent();
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(intent, default));
        var recovered = await adapter.ReconcileAsync(intent, default);
        Assert.Equal(DeliveryStatus.Succeeded, recovered.Status);
        Assert.Equal("101", recovered.ExternalId);
        Assert.Single(fixture.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task UnknownStatusOutcomeNeverRetriesMutation()
    {
        var fixture = new ApiFixture();
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(StatusIntent(), default)).Status);
        Assert.All(fixture.Requests, request => Assert.Equal("GET", request.Method));
    }

    [Fact]
    public async Task SpoofedCommentIsNotAdoptedAndMultipleOwnedCommentsFailClosed()
    {
        var fixture = new ApiFixture();
        fixture.Comments.Add((88, 55, StatusIntent().StatusBody!));
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        await adapter.ExecuteAsync(StatusIntent(), default);
        Assert.Equal(2, fixture.Comments.Count);
        fixture.Comments.Add((102, 99, StatusIntent().StatusBody!));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReconcileAsync(StatusIntent(), default));
        Assert.Single(fixture.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task ProjectionUpdatesOwnedCommentInsteadOfCreatingAnother()
    {
        var fixture = new ApiFixture();
        fixture.Comments.Add((101, 99, StatusIntent().StatusBody!));
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        var next = StatusIntent();
        next.StatusBody = StatusProjection.Render(new IssueState { Number = 7, Phase = Phase.HUMAN_REVIEW });
        await adapter.ExecuteAsync(next, default);
        Assert.Single(fixture.Comments);
        Assert.Contains(fixture.Requests, request => request.Method == "PATCH");
        Assert.DoesNotContain(fixture.Requests, request => request.Method == "POST");
    }

    [Fact]
    public async Task LabelProjectionPreservesUnrelatedAndConcurrentHumanLabels()
    {
        var fixture = new ApiFixture();
        fixture.Labels.Add("bug");
        fixture.Labels.Add("agent:review");
        fixture.AddConcurrentLabel = true;
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99, true);
        var intent = new EffectIntent
        {
            Kind = EffectKind.ProjectLabels, IssueNumber = 7, Generation = 1, Labels = ["agent:working"]
        };
        var result = await adapter.ExecuteAsync(intent, default);
        Assert.Equal(DeliveryStatus.Succeeded, result.Status);
        Assert.Contains("bug", fixture.Labels);
        Assert.Contains("human-added-during-write", fixture.Labels);
        Assert.Contains("agent:working", fixture.Labels);
        Assert.DoesNotContain("agent:review", fixture.Labels);
        Assert.DoesNotContain(fixture.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task ProjectionWritesDisabledByDefaultAndCannotExecuteOtherEffects()
    {
        var fixture = new ApiFixture();
        using var client = fixture.Client();
        var adapter = new GitHubProjectionAdapter(new(client, "owner/repo", 1), 99);
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(StatusIntent(), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(new EffectIntent
            { Kind = EffectKind.LaunchTask, IssueNumber = 7, Generation = 1 }, default));
        Assert.Empty(fixture.Requests);
    }

    private static EffectIntent StatusIntent() => new()
    {
        Kind = EffectKind.ProjectStatus, IssueNumber = 7, Generation = 1,
        StatusBody = StatusProjection.Render(new IssueState { Number = 7, Phase = Phase.CODING })
    };

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ChangingStore : IStateStore
    {
        public int Reads { get; private set; }
        private RepositoryState state = RepositoryState.Create("owner/repo");

        public Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            if (Reads == 2) state.Issues[8] = new IssueState { Number = 8 };
            return Task.FromResult(new StateSnapshot(Reads.ToString(), StateJson.Deserialize(StateJson.Serialize(state))));
        }

        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The bounded command must reject the contaminated snapshot before saving.");
    }

    private sealed class NoRequests : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Out-of-scope wake must be rejected before API verification.");
    }

    private sealed class Store(RepositoryState state) : IStateStore
    {
        public RepositoryState State { get; private set; } = state;
        public Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StateSnapshot(State.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(State))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken cancellationToken)
        {
            if (expected.Revision != State.Version.ToString()) return Task.FromResult(false);
            State = StateJson.Deserialize(StateJson.Serialize(next));
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyEvents : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken cancellationToken) =>
            Task.FromResult(new WorkerEventBatch([]));
    }

    private sealed class Observations(GitHubRepositoryApi api) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken) =>
            api.ObserveAsync(state, Now, cancellationToken);
    }

    private static JsonElement Notification() => JsonSerializer.SerializeToElement(new
    {
        repository = new { id = 1, full_name = "owner/repo" },
        sender = new { id = 55, login = "maintainer", type = "User" },
        issue = new { id = 70, number = 7 }, action = "labeled", label = new { name = "agent:ready" }
    });

    private sealed class ApiFixture : HttpMessageHandler
    {
        public RepositoryState State { get; } = RepositoryState.Create("owner/repo");
        public List<(string Method, string Path)> Requests { get; } = [];
        public List<(long Id, long Actor, string Body)> Comments { get; } = [];
        public List<string> Labels { get; } = ["agent:ready"];
        public string? Corruption { get; init; }
        public bool MissingState { get; init; }
        public bool BotNotification { get; init; }
        public bool LoseWriteResponse { get; init; }
        public bool AddConcurrentLabel { get; set; }
        public bool Closed { get; set; }
        public bool RepeatFullCommentPage { get; init; }
        public bool OversizedComments { get; init; }
        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://api.github.com/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path));
            const string prefix = "/repos/owner/repo/";
            object result;
            if (path == prefix + "git/ref/heads/agent-state")
            {
                if (MissingState) return new(HttpStatusCode.NotFound);
                result = new { @object = new { sha = Sha } };
            }
            else if (path == prefix + "git/commits/" + Sha) result = new { tree = new { sha = Sha } };
            else if (path == prefix + "git/trees/" + Sha)
                result = new { tree = new[] { new { path = "state.json", type = "blob", mode = "100644", sha = Sha } } };
            else if (path == prefix + "git/blobs/" + Sha)
                result = new { encoding = "base64", size = 100, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(StateJson.Serialize(State))) };
            else if (path == "/repos/owner/repo")
                result = new { id = Corruption == "repository" ? 2 : 1, full_name = "owner/repo" };
            else if (path == prefix + "actions/runs/44") result = new
            {
                @event = "issues", path = Corruption == "workflow" ? "untrusted.yml" : ".github/workflows/agent-state-machine.yml",
                actor = new { id = BotNotification ? GitHubLiveCredentials.ProjectionActorId : Corruption == "run-actor" ? 66 : 55 }
            };
            else if (Uri.UnescapeDataString(path) == "/users/github-actions[bot]")
                result = new { id = GitHubLiveCredentials.ProjectionActorId, type = "Bot" };
            else if (path == "/users/maintainer") result = new { id = Corruption == "actor" ? 66 : 55, type = "User" };
            else if (path == prefix + "collaborators/maintainer/permission")
                result = new { permission = Corruption == "permission" ? "read" : "write", user = new { id = Corruption == "permission-user" ? 66 : 55 } };
            else if (path == prefix + "issues/7")
                result = new { id = Corruption == "issue" ? 71 : 70, number = 7, state = Closed ? "closed" : "open", labels = Labels.Select(name => new { name }) };
            else if (path.StartsWith(prefix + "issues/", StringComparison.Ordinal) &&
                     !path[(prefix + "issues/").Length..].Contains("/", StringComparison.Ordinal) &&
                     int.TryParse(path[(prefix + "issues/").Length..], out var issueNumber))
                result = new { id = issueNumber * 10, number = issueNumber, state = "open", labels = Array.Empty<object>() };
            else if (path == prefix + "pulls/17")
                result = new { number = 17, state = "open", merged = false, head = new { sha = Sha }, @base = new { repo = new { id = 1 } } };
            else if (path == prefix + "issues/7/labels" && request.Method == HttpMethod.Get)
                result = Labels.Select(name => new { name }).ToArray();
            else if (path == prefix + "issues/7/labels" && request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Labels.AddRange(body.RootElement.GetProperty("labels").EnumerateArray().Select(label => label.GetString()!));
                if (AddConcurrentLabel) Labels.Add("human-added-during-write");
                result = Labels.Select(name => new { name }).ToArray();
            }
            else if (path.StartsWith(prefix + "issues/7/labels/") && request.Method == HttpMethod.Delete)
            {
                Labels.Remove(Uri.UnescapeDataString(path[(prefix + "issues/7/labels/").Length..]));
                result = Labels.Select(name => new { name }).ToArray();
            }
            else if (path == prefix + "issues/7/comments" && request.Method == HttpMethod.Get)
            {
                if (OversizedComments)
                    return new(HttpStatusCode.OK) { Content = new StringContent(new string(' ', 2 * 1024 * 1024 + 1)) };
                result = RepeatFullCommentPage
                    ? Enumerable.Range(1, 100).Select(id => new { id = (long)id, body = "other", user = new { id = 55L } }).ToArray()
                    : Comments.Select(comment => new { id = comment.Id, body = comment.Body, user = new { id = comment.Actor } }).ToArray();
            }
            else if (request.Method == HttpMethod.Post && path == prefix + "issues/7/comments" ||
                     request.Method == HttpMethod.Patch && path == prefix + "issues/comments/101")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var text = body.RootElement.GetProperty("body").GetString()!;
                Comments.RemoveAll(comment => comment.Id == 101);
                Comments.Add((101, 99, text));
                if (LoseWriteResponse) throw new HttpRequestException("Lost response after create.");
                result = new { id = 101, body = text, user = new { id = 99 } };
            }
            else throw new InvalidOperationException($"Unexpected fixture route: {request.Method} {path}");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
}
