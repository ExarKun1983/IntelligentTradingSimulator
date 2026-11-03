using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using Xunit;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class ContinuationIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T22:00:00Z");
    private const string Definition = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task YieldPersistsTicketAndAnotherInvocationConsumesCanonicalWorkWithoutNewStart()
    {
        var store = new Store(RepositoryState.Create("owner/repo"));
        var worker = new OrchestrationWorker(new(new Allow()), new Clock());
        var effects = new Effects();
        var source = new Source([new("ready", "owner/repo", 7, 1, 1, new("human", ActorKind.Human), Now, new Ready())]);
        await worker.RunAsync(store, source, new Observation(), effects, new(false, true, true, true),
            new() { MaximumIterations = 1, PollInterval = TimeSpan.Zero });
        Assert.Equal(1, effects.Launches);
        Assert.NotNull(store.Value.Continuation);
        Assert.True(store.Value.Continuation!.Pending);
        var ticket = store.Value.Continuation.Id;
        Assert.Null(store.Value.WorkerLease);
        await worker.RunAsync(store, new Source([]), new Observation(), effects, new(false, true, true, true),
            new() { MaximumIterations = 1, PollInterval = TimeSpan.Zero });
        Assert.Equal(1, effects.Launches);
        Assert.Equal(ticket, store.Value.Continuation!.Id);
        Assert.Equal(Phase.CODING, store.Value.Issues[7].Phase);
        Assert.True(OrchestrationContinuation.IsDue(store.Value, Now + TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public async Task InterruptionKeepsRecoveryTicketAndExpiredLeaseCannotCauseDuplicateLaunch()
    {
        var store = new Store(RepositoryState.Create("owner/repo"));
        var effects = new Effects();
        var worker = new OrchestrationWorker(new(new Allow()), new Clock());
        await Assert.ThrowsAsync<IOException>(() => worker.RunAsync(store, new ThrowingSource(), new Observation(), effects,
            new(false, true, true, true), new() { MaximumIterations = 1 }));
        Assert.NotNull(store.Value.Continuation);
        Assert.Null(store.Value.WorkerLease);
        Assert.Equal(0, effects.Launches);
        store.Value.WorkerLease = new("crashed", Now - TimeSpan.FromSeconds(1));
        await worker.RunAsync(store, new Source([]), new Observation(), effects, new(false, true, true, true),
            new() { MaximumIterations = 1 });
        Assert.Null(store.Value.WorkerLease);
        Assert.False(store.Value.Continuation!.Pending);
        Assert.Equal(0, effects.Launches);
    }

    [Fact]
    public void ContinuationNeverClearsAHumanHoldOrCreatesReadiness()
    {
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new() { Number = 7, Phase = Phase.BLOCKED, ReactivationRequired = true, HoldReason = "Human decision." };
        var scheduled = OrchestrationContinuation.Prepare(state, Now, "Running.", true);
        var settled = OrchestrationContinuation.Prepare(scheduled, Now, "Held.");
        Assert.False(settled.Continuation!.Pending);
        Assert.Equal(Phase.BLOCKED, settled.Issues[7].Phase);
        Assert.Equal("Human decision.", settled.Issues[7].HoldReason);
        Assert.False(settled.Issues[7].Ready);
        Assert.False(OrchestrationContinuation.IsDue(settled, Now + TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("workflow")]
    [InlineData("sha")]
    [InlineData("repository")]
    [InlineData("path")]
    [InlineData("event")]
    public async Task ForgedWakeRunCannotAuthorizeObservation(string corruption)
    {
        using var client = new HttpClient(new Handler(corruption)) { BaseAddress = new("https://api.github.com/") };
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), new(10, Definition));
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.VerifyAsync(payload.RootElement, "schedule", "90", RepositoryState.Create("owner/repo"), default));
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("issue_comment")]
    [InlineData("pull_request_target")]
    public async Task SupportedWakeUsesTrustedRunNotPayloadProseHeadOrActor(string eventName)
    {
        using var client = new HttpClient(new Handler("", eventName)) { BaseAddress = new("https://api.github.com/") };
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), new(10, Definition));
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"},"comment":{"body":"ready complete green approved"},"pull_request":{"head":{"sha":"fake"}}}""");
        var state = RepositoryState.Create("owner/repo");
        Assert.True(await source.VerifyAsync(payload.RootElement, eventName, "90", state, default));
        Assert.Empty(state.Issues);
        Assert.Empty(state.Inbox);
        Assert.Empty(state.Outbox);
    }

    [Fact]
    public async Task DirectPrReviewWakeCannotUsePrivilegedDefaultBranchComposition()
    {
        var handler = new Handler("", "pull_request_review");
        using var client = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), new(10, Definition));
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => source.VerifyAsync(payload.RootElement,
            "pull_request_review", "90", RepositoryState.Create("owner/repo"), default));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task DefaultDisabledWakeHostMakesNoApiRequestsOrWrites()
    {
        var handler = new Handler();
        using var client = new HttpClient(handler) { BaseAddress = new("https://api.github.com/") };
        var store = new Store(RepositoryState.Create("owner/repo"));
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"}}""");
        var host = new GitHubOrchestrationHost(new(client, "owner/repo", 1), store);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.ReconcileWakeAsync(payload.RootElement, "schedule", "90", new(10, Definition), new(), 2, "master", 2));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void EmptyLiveSettingsCannotEnableExecution()
    {
        Assert.Throws<InvalidOperationException>(() => GitHubLiveSettings.Read(_ => null));
        Assert.Throws<InvalidDataException>(() => GitHubLiveSettings.Read(_ => "true"));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("identity")]
    [InlineData("deadline")]
    [InlineData("lease")]
    public void MalformedCanonicalContinuationFailsClosed(string corruption)
    {
        var state = OrchestrationContinuation.Prepare(RepositoryState.Create("owner/repo"), Now, "In flight.", true);
        state.Continuation = corruption switch
        {
            "generation" => state.Continuation! with { RepositoryGeneration = 2 },
            "identity" => state.Continuation! with { Id = "" },
            "deadline" => state.Continuation! with { DueAt = default },
            _ => state.Continuation
        };
        if (corruption == "lease") state.WorkerLease = new("", Now);
        Assert.Throws<InvalidDataException>(() => OrchestrationContinuation.IsDue(state, Now));
    }

    private sealed class Allow : IAuthorizationPolicy { public bool IsAuthorized(OrchestrationEvent message) => true; }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Source(List<OrchestrationEvent> events) : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token) =>
            Task.FromResult(new WorkerEventBatch(events.Where(message => !state.Inbox.ContainsKey(message.Id)).ToList()));
    }
    private sealed class ThrowingSource : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token) => throw new IOException("Offline interruption");
    }
    private sealed class Observation : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) =>
            Task.FromResult(new LiveObservation { ObservedAt = Now, Issues = new() { [7] = new() { Ready = true } } });
    }
    private sealed class Effects : IVerifiedAgentEffectAdapter
    {
        public bool AgentEffectsAuthorized => true;
        public int Launches { get; private set; }
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
        {
            if (intent.Kind == EffectKind.LaunchTask) Launches++;
            return Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "remote-" + intent.Id));
        }
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) =>
            Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "remote-" + intent.Id));
    }
    private sealed class Store(RepositoryState state) : IStateStore
    {
        public RepositoryState Value { get; set; } = state;
        public int Writes { get; private set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token) =>
            Task.FromResult(new StateSnapshot(Value.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(Value))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            if (Value.Version.ToString() != expected.Revision) return Task.FromResult(false);
            Value = StateJson.Deserialize(StateJson.Serialize(next));
            Writes++;
            return Task.FromResult(true);
        }
    }
    private sealed class Handler(string corruption = "", string eventName = "schedule") : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            object response = request.RequestUri!.AbsolutePath == "/repos/owner/repo" ? new { id = 1, full_name = "owner/repo" } :
                new { id = corruption == "id" ? 91 : 90, workflow_id = corruption == "workflow" ? 11 : 10,
                    head_sha = corruption == "sha" ? new string('b', 40) : Definition,
                    path = corruption == "path" ? "other.yml" : ".github/workflows/agent-state-machine.yml",
                    @event = corruption == "event" ? "push" : eventName, repository = new { id = corruption == "repository" ? 2 : 1 } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
