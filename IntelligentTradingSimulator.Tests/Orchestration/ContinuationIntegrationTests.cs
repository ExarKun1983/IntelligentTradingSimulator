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
    private const string Execution = "cccccccccccccccccccccccccccccccccccccccc";
    private static GitHubWakeRoute TrustedRoute => new(10, Definition)
    {
        WorkflowExecutionRef = "owner/repo/.github/workflows/agent-state-machine.yml@refs/heads/master",
        WorkflowExecutionSha = Definition
    };

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
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), TrustedRoute);
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"}}""");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            source.VerifyAsync(payload.RootElement, "schedule", "90", RepositoryState.Create("owner/repo"), default));
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("issue_comment")]
    public async Task SupportedWakeUsesTrustedRunNotPayloadProseHeadOrActor(string eventName)
    {
        using var client = new HttpClient(new Handler("", eventName)) { BaseAddress = new("https://api.github.com/") };
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), TrustedRoute);
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"},"comment":{"body":"ready complete green approved"},"pull_request":{"head":{"sha":"fake"}}}""");
        var state = RepositoryState.Create("owner/repo");
        Assert.True(await source.VerifyAsync(payload.RootElement, eventName, "90", state, default));
        Assert.Empty(state.Issues);
        Assert.Empty(state.Inbox);
        Assert.Empty(state.Outbox);
    }

    [Fact]
    public async Task PullRequestWakeUsesTrustedWorkflowShaInsteadOfPullRequestHead()
    {
        var head = new string('b', 40);
        using var client = new HttpClient(new Handler("", "pull_request_target", head))
            { BaseAddress = new("https://api.github.com/") };
        var route = new GitHubWakeRoute(10, Definition)
        {
            WorkflowExecutionRef = "owner/repo/.github/workflows/agent-state-machine.yml@refs/heads/master",
            WorkflowExecutionSha = Definition
        };
        var source = new GitHubWakeSource(new(client, "owner/repo", 1), route);
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            pull_request = new { head = new { sha = head } }
        });
        var state = RepositoryState.Create("owner/repo");

        Assert.True(await source.VerifyAsync(payload, "pull_request_target", "90", state, default));
        Assert.Empty(state.Inbox);
        Assert.Empty(state.Outbox);
    }

    [Theory]
    [InlineData("missing-workflow-sha")]
    [InlineData("wrong-workflow-sha")]
    [InlineData("wrong-workflow-ref")]
    [InlineData("missing-workflow-ref")]
    [InlineData("wrong-pull-head")]
    [InlineData("invalid-pull-head")]
    public async Task PullRequestWakeRejectsUntrustedDefinitionOrMismatchedRunHead(string corruption)
    {
        var head = new string('b', 40);
        using var client = new HttpClient(new Handler("", "pull_request_target", head))
            { BaseAddress = new("https://api.github.com/") };
        var route = new GitHubWakeRoute(10, Definition)
        {
            WorkflowExecutionRef = corruption switch
            {
                "missing-workflow-ref" => null,
                "wrong-workflow-ref" => "owner/repo/.github/workflows/agent-state-machine.yml@refs/heads/other",
                _ => "owner/repo/.github/workflows/agent-state-machine.yml@refs/heads/master"
            },
            WorkflowExecutionSha = corruption switch
            {
                "missing-workflow-sha" => null,
                "wrong-workflow-sha" => "invalid",
                _ => Definition
            }
        };
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            pull_request = new
            {
                head = new { sha = corruption == "wrong-pull-head" ? Definition : corruption == "invalid-pull-head" ? "bad" : head }
            }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubWakeSource(new(client, "owner/repo", 1), route)
                .VerifyAsync(payload, "pull_request_target", "90", RepositoryState.Create("owner/repo"), default));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("workflow")]
    [InlineData("repository")]
    [InlineData("path")]
    [InlineData("event")]
    public async Task PullRequestWakeStillRejectsForgedRunIdentity(string corruption)
    {
        var head = new string('b', 40);
        using var client = new HttpClient(new Handler(corruption, "pull_request_target", head))
            { BaseAddress = new("https://api.github.com/") };
        var route = new GitHubWakeRoute(10, Definition)
        {
            WorkflowExecutionRef = "owner/repo/.github/workflows/agent-state-machine.yml@refs/heads/master",
            WorkflowExecutionSha = Definition
        };
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            pull_request = new { head = new { sha = head } }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubWakeSource(new(client, "owner/repo", 1), route)
                .VerifyAsync(payload, "pull_request_target", "90", RepositoryState.Create("owner/repo"), default));
    }

    [Theory]
    [InlineData("issue_comment")]
    [InlineData("pull_request_target")]
    public async Task WakeAcceptsNewMasterCommitWhenWorkflowFileIsUnchanged(string eventName)
    {
        var head = new string('b', 40);
        var runSha = eventName == "pull_request_target" ? head : Execution;
        using var client = new HttpClient(new Handler("", eventName, runSha))
            { BaseAddress = new("https://api.github.com/") };
        var route = TrustedRoute with { WorkflowExecutionSha = Execution };
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            pull_request = new { head = new { sha = head } }
        });

        Assert.True(await new GitHubWakeSource(new(client, "owner/repo", 1), route)
            .VerifyAsync(payload, eventName, "90", RepositoryState.Create("owner/repo"), default));
    }

    [Fact]
    public async Task ManagedCiWakeStillRequiresTheSavedUpstreamRunAfterMasterChanges()
    {
        using var client = new HttpClient(new Handler("", "workflow_run", Execution))
            { BaseAddress = new("https://api.github.com/") };
        var state = RepositoryState.Create("owner/repo");
        state.Issues[7] = new IssueState
        {
            Number = 7,
            Ci = new CiIdentity
            {
                DispatchBoundary = new CiDispatchBoundary(20, Definition, "refs/tags/ci", "correlation", Now, 91)
            }
        };
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            action = "completed",
            workflow_run = new { id = 91 }
        });

        Assert.True(await new GitHubWakeSource(new(client, "owner/repo", 1),
            TrustedRoute with { WorkflowExecutionSha = Execution })
            .VerifyAsync(payload, "workflow_run", "90", state, default));
    }

    [Theory]
    [InlineData("different")]
    [InlineData("missing")]
    [InlineData("symlink")]
    [InlineData("truncated")]
    [InlineData("wrong-tree")]
    [InlineData("wrong-commit")]
    public async Task WakeRejectsUntrustedOrIncompleteWorkflowDefinition(string problem)
    {
        var head = new string('b', 40);
        using var client = new HttpClient(new Handler("", "pull_request_target", head, problem))
            { BaseAddress = new("https://api.github.com/") };
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            pull_request = new { head = new { sha = head } }
        });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new GitHubWakeSource(new(client, "owner/repo", 1), TrustedRoute with { WorkflowExecutionSha = Execution })
                .VerifyAsync(payload, "pull_request_target", "90", RepositoryState.Create("owner/repo"), default));
    }

    [Theory]
    [InlineData("issues")]
    [InlineData("workflow_dispatch")]
    public async Task LiveCommandAcceptsNewMasterCommitOnlyForUnchangedWorkflow(string eventName)
    {
        using var client = new HttpClient(new Handler("", eventName, Execution))
            { BaseAddress = new("https://api.github.com/") };
        var route = TrustedRoute with { WorkflowExecutionSha = Execution };

        Assert.Equal(Now, await GitHubLiveCommand.VerifyLiveCommandDefinitionAsync(
            new(client, "owner/repo", 1), route, "90", eventName));
    }

    [Fact]
    public async Task LiveCommandRejectsRunFromAnotherCommitEvenWhenWorkflowIsUnchanged()
    {
        using var client = new HttpClient(new Handler("", "issues", Definition))
            { BaseAddress = new("https://api.github.com/") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubLiveCommand.VerifyLiveCommandDefinitionAsync(new(client, "owner/repo", 1),
                TrustedRoute with { WorkflowExecutionSha = Execution }, "90", "issues"));
    }

    [Fact]
    public async Task LiveCommandRejectsChangedWorkflowFile()
    {
        using var client = new HttpClient(new Handler("", "issues", Execution, "different"))
            { BaseAddress = new("https://api.github.com/") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            GitHubLiveCommand.VerifyLiveCommandDefinitionAsync(new(client, "owner/repo", 1),
                TrustedRoute with { WorkflowExecutionSha = Execution }, "90", "issues"));
    }

    [Fact]
    public async Task WakeFailsClosedWhenWorkflowTreeIsUnavailable()
    {
        using var client = new HttpClient(new Handler("", "issue_comment", Execution, "unavailable"))
            { BaseAddress = new("https://api.github.com/") };
        using var payload = JsonDocument.Parse("""{"repository":{"id":1,"full_name":"owner/repo"}}""");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubWakeSource(new(client, "owner/repo", 1), TrustedRoute with { WorkflowExecutionSha = Execution })
                .VerifyAsync(payload.RootElement, "issue_comment", "90", RepositoryState.Create("owner/repo"), default));
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
    [InlineData("budget")]
    [InlineData("pending-budget")]
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
        if (corruption is "budget" or "pending-budget")
            state.Issues[7] = new IssueState
            {
                Number = 7,
                Tasks = new() { ["task"] = new TaskIdentity
                    { Id = "task", AutomaticContinuations = corruption == "budget" ? 4 : 3,
                        ContinuationPending = corruption == "pending-budget" } }
            };
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
    private sealed class Handler(string corruption = "", string eventName = "schedule", string? headSha = null,
        string? workflowProblem = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            object response;
            if (path == "/repos/owner/repo") response = new { id = 1, full_name = "owner/repo" };
            else if (path.EndsWith("/actions/runs/91", StringComparison.Ordinal))
                response = new { id = 91, workflow_id = 20, head_sha = Definition,
                    path = GitHubCiAdapter.WorkflowPath, @event = "workflow_dispatch", repository = new { id = 1 } };
            else if (path.Contains("/git/commits/"))
            {
                var sha = path.Split('/')[^1];
                var tree = sha == Definition ? new string('d', 40) : new string('e', 40);
                response = new { sha = workflowProblem == "wrong-commit" ? Execution : sha, tree = new { sha = tree } };
            }
            else if (path.Contains("/git/trees/"))
            {
                if (workflowProblem == "unavailable")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var sha = path.Split('/')[^1];
                var blob = workflowProblem == "different" && sha == new string('e', 40)
                    ? new string('f', 40) : new string('1', 40);
                var entries = workflowProblem == "missing" ? Array.Empty<object>() :
                    [new { path = ".github/workflows/agent-state-machine.yml", type = "blob",
                        mode = workflowProblem == "symlink" ? "120000" : "100644", sha = blob }];
                response = new { sha = workflowProblem == "wrong-tree" ? new string('f', 40) : sha,
                    truncated = workflowProblem == "truncated", tree = entries };
            }
            else response = new { id = corruption == "id" ? 91 : 90, workflow_id = corruption == "workflow" ? 11 : 10,
                    head_sha = corruption == "sha" ? new string('b', 40) : headSha ?? Definition,
                    path = corruption == "path" ? "other.yml" : ".github/workflows/agent-state-machine.yml",
                    @event = corruption == "event" ? "push" : eventName, repository = new { id = corruption == "repository" ? 2 : 1 },
                    created_at = Now };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
