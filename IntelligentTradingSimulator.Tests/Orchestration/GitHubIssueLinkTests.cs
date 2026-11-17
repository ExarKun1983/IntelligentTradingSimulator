using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubIssueLinkTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandoffAddsOrRecognizesClosingRelationshipWithoutEditingOrMergingPull(bool alreadyLinked)
    {
        var store = Executing();
        using var api = new Api { Linked = alreadyLinked };
        using var client = api.Client();
        var adapter = new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, true);

        Assert.Equal(new(DeliveryStatus.Succeeded, "PR_17"), await adapter.ExecuteAsync(Intent(store.State), default));
        Assert.True(api.Linked);
        Assert.Equal(alreadyLinked ? 0 : 1, api.Mutations);
        Assert.Equal(Phase.HUMAN_REVIEW, store.State.Issues[7].Phase);
        Assert.All(api.Writes, write => Assert.Equal("POST /graphql", write));
        Assert.DoesNotContain(api.Bodies, body => body.Contains("\"body\"") || body.Contains("mergePullRequest"));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("fork")]
    [InlineData("repository")]
    [InlineData("closed-pull")]
    [InlineData("closed-issue")]
    [InlineData("other-link")]
    [InlineData("multiple-links")]
    [InlineData("pagination")]
    [InlineData("graphql-error")]
    public async Task ChangedOrAmbiguousLiveMappingNeverMutatesLinks(string corruption)
    {
        var store = Executing();
        using var api = new Api { Corruption = corruption };
        using var client = api.Client();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, true)
                .ExecuteAsync(Intent(store.State), default));

        Assert.Equal(0, api.Mutations);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("feedback")]
    [InlineData("ci")]
    [InlineData("review")]
    [InlineData("shared-pr")]
    [InlineData("active")]
    [InlineData("review-incomplete")]
    [InlineData("disabled")]
    public async Task MissingAuthorizationOrEligibilityPreventsIo(string change)
    {
        var store = Executing();
        var issue = store.State.Issues[7];
        switch (change)
        {
            case "paused": issue.Paused = true; break;
            case "feedback": issue.PendingFeedbackEventIds.Add("feedback"); break;
            case "ci": issue.Ci!.Green = false; break;
            case "review": issue.Review!.Classification = ReviewClassification.NeedsHuman; break;
            case "shared-pr": store.State.Issues[8] = new() { Number = 8, PullRequest = 17 }; break;
            case "active": issue.ActiveAuthorized = true; break;
            case "review-incomplete": issue.Review!.LifecycleState = ReviewLifecycleState.Failed; break;
        }
        using var api = new Api();
        using var client = api.Client();

        var adapter = new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, change != "disabled");
        if (change == "disabled")
            await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(Intent(store.State), default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(Intent(store.State), default));

        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostMutationResponseIsLookupOnlyEvenAfterPullCloses(bool merged)
    {
        var store = Executing();
        using var api = new Api { LoseMutationResponse = true };
        using var client = api.Client();
        var adapter = new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, true);
        var intent = Intent(store.State);
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(intent, default));
        Assert.True(await store.TrySaveAsync(await store.LoadAsync(default),
            new OrchestrationReducer().RecordOutcome(store.State, intent.Id, new(DeliveryStatus.Unknown), Now), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(intent, default));
        api.Corruption = merged ? "closed-pull" : null;

        Assert.Equal(new(DeliveryStatus.Succeeded, "PR_17"), await adapter.ReconcileAsync(intent, default));
        api.Linked = false;
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(intent, default)).Status);
        Assert.Equal(1, api.Mutations);
    }

    [Fact]
    public async Task FeedbackArrivingDuringLookupPreventsMutation()
    {
        var store = Executing();
        using var api = new Api { OnLookup = () => store.State.Issues[7].PendingFeedbackEventIds.Add("new-feedback") };
        using var client = api.Client();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, true)
                .ExecuteAsync(Intent(store.State), default));

        Assert.Equal(0, api.Mutations);
    }

    [Fact]
    public async Task MutationErrorsRemainUnknownWithoutBeingReplayed()
    {
        var store = Executing();
        using var api = new Api { Corruption = "mutation-error" };
        using var client = api.Client();
        var adapter = new GitHubIssueLinkAdapter(new(client, "owner/repo", 1), store, true);
        var intent = Intent(store.State);

        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(intent, default));
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(intent, default)).Status);
        Assert.Equal(1, api.Mutations);
    }

    [Fact]
    public void ProjectionIsStableDoesNotStealAdmissionAndIsCancelledOnFeedback()
    {
        var state = State();
        state.Issues[8] = new() { Number = 8, Phase = Phase.CODING, ActiveAuthorized = true };
        state.AdmittedIssue = 8;
        var reducer = new OrchestrationReducer();
        state = reducer.Reconcile(state, Live()).State;
        var id = Intent(state).Id;
        state = reducer.Reconcile(state, Live()).State;
        Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue);
        state = reducer.MarkExecuting(state, id, Now);
        Assert.Equal(8, state.AdmittedIssue);
        state = reducer.RecordOutcome(state, id, new(DeliveryStatus.Succeeded, "PR_17"), Now);
        Assert.Equal(8, state.AdmittedIssue);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(state).Status);

        var pending = reducer.Reconcile(State(), Live()).State;
        pending.Issues[7].PendingFeedbackEventIds.Add("feedback");
        pending = reducer.Reconcile(pending, Live()).State;
        Assert.Equal(DeliveryStatus.Cancelled, Intent(pending).Status);
    }

    [Theory]
    [InlineData(Phase.REVIEW_PENDING)]
    [InlineData(Phase.BLOCKED)]
    [InlineData(Phase.CLOSED)]
    public void LinksAreNotPlannedBeforeAcceptanceOrForStoppedWork(Phase phase)
    {
        var state = State();
        state.Issues[7].Phase = phase;
        Assert.False(IssueLinkPolicy.Eligible(state, state.Issues[7]));
        Assert.DoesNotContain(new OrchestrationReducer().Reconcile(state, Live()).State.Outbox.Values,
            effect => effect.Kind == EffectKind.LinkIssue);
    }

    private static EffectIntent Intent(RepositoryState state) =>
        Assert.Single(state.Outbox.Values, intent => intent.Kind == EffectKind.LinkIssue);

    private static GitHubTaskIntegrationTests.Store Executing()
    {
        var reducer = new OrchestrationReducer();
        var state = reducer.Reconcile(State(), Live()).State;
        return new(reducer.MarkExecuting(state, Intent(state).Id, Now));
    }

    private static LiveObservation Live() => new()
    {
        ObservedAt = Now, Issues = new() { [7] = new() { PullRequest = 17, HeadSha = Sha } }
    };

    private static RepositoryState State()
    {
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new()
        {
            Number = 7, Phase = Phase.HUMAN_REVIEW, HoldReason = "Awaiting human approval.",
            ReactivationRequired = true, PullRequest = 17, HeadSha = Sha, Round = 1, ActiveTaskId = "task",
            Tasks = new() { ["task"] = new() { Id = "task", Status = TaskStatus.Completed,
                LaunchConfirmed = true, FinalSha = Sha, Round = 1 } },
            Ci = new() { IntentId = "ci", TaskId = "task", Sha = Sha, Round = 1, Green = true, RunId = "90",
                DispatchBoundary = new(10, new('b', 40), "refs/tags/trusted", "correlation", Now, 90) },
            Review = new() { TaskId = "task", Sha = Sha, Round = 1,
                LifecycleState = ReviewLifecycleState.Completed, Classification = ReviewClassification.Accepted }
        };
        state.Outbox["ci"] = new() { Id = "ci", IssueNumber = 7, Generation = 1, TaskId = "task",
            Sha = Sha, Round = 1, Kind = EffectKind.RunCi, Status = DeliveryStatus.Succeeded, ExternalId = "90" };
        return state;
    }

    private sealed class Api : HttpMessageHandler
    {
        public string? Corruption { get; set; }
        public bool Linked { get; set; }
        public bool LoseMutationResponse { get; init; }
        public Action? OnLookup { get; init; }
        public int Mutations { get; private set; }
        public List<string> Requests { get; } = [];
        public List<string> Writes { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpClient Client() => new(this, false) { BaseAddress = new("https://api.github.com/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path);
            object value;
            if (request.Method != HttpMethod.Get)
            {
                Writes.Add(request.Method + " " + path);
                Assert.Equal("/graphql", path);
                var body = await request.Content!.ReadAsStringAsync(token);
                Bodies.Add(body);
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                Assert.Equal("PR_17", root.GetProperty("variables").GetProperty("pull").GetString());
                if (root.GetProperty("query").GetString()!.StartsWith("mutation", StringComparison.Ordinal))
                {
                    Assert.Equal("I_7", root.GetProperty("variables").GetProperty("issue").GetString());
                    Mutations++;
                    Linked = Corruption != "mutation-error";
                    if (LoseMutationResponse) throw new HttpRequestException("offline lost response");
                    value = Corruption == "mutation-error" ? (object)new { errors = new[] { new { message = "offline error" } } } :
                        new { data = new { addCloseIssueReferences = new { issue = new { id = "I_7" } } } };
                }
                else
                {
                    OnLookup?.Invoke();
                    value = Corruption == "graphql-error" ? (object)new { errors = new[] { new { message = "offline error" } } } :
                        new { data = new { node = new { id = "PR_17", closingIssuesReferences = new
                        {
                            nodes = Corruption == "multiple-links" ? new[] { new { id = "I_7" }, new { id = "I_8" } } :
                                Corruption == "other-link" ? new[] { new { id = "I_8" } } :
                                Linked ? new[] { new { id = "I_7" } } : [],
                            pageInfo = new { hasNextPage = Corruption == "pagination" }
                        } } } };
                }
            }
            else if (path == "/repos/owner/repo")
                value = new { id = Corruption == "repository" ? 2 : 1, full_name = "owner/repo", default_branch = "master" };
            else if (path.EndsWith("/issues/7", StringComparison.Ordinal))
                value = new { number = 7, node_id = "I_7", state = Corruption == "closed-issue" ? "closed" : "open" };
            else if (path.EndsWith("/pulls/17", StringComparison.Ordinal))
                value = new
                {
                    number = 17, node_id = "PR_17", state = Corruption == "closed-pull" ? "closed" : "open",
                    @base = new { @ref = Corruption == "base" ? "other" : "master", repo = new { id = 1 } },
                    head = new { sha = Corruption == "head" ? new string('b', 40) : Sha,
                        repo = new { id = Corruption == "fork" ? 2 : 1 } }
                };
            else throw new InvalidOperationException("Unexpected offline request: " + path);
            return new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }
    }
}
