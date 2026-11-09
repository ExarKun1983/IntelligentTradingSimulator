using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class TaskTerminalHostIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T22:00:00Z");
    private static readonly GitHubExecutionApproval Approval = new(true, true, true, true);

    [Fact]
    public async Task VerifiedHostPersistsApiEvidenceBeforeOperatorResolutionAndDoesNotAdvance()
    {
        var store = new Store();
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        var result = await Run(client, store);
        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        AssertResolved(store.State);
        var evidenced = Assert.Single(store.Saves, state =>
            state.Inbox.Values.Any(entry => entry.Event?.Data is TaskTerminalObserved) &&
            !state.Inbox.Values.Any(entry => entry.Event?.Data is ResolveTaskTerminal));
        Assert.Equal(7, evidenced.AdmittedIssue);
        Assert.Equal(TaskStatus.Unknown, evidenced.Issues[7].Tasks["task"].Status);
        var command = Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is ResolveTaskTerminal).Event!;
        Assert.Equal(Now, command.OccurredAt);
        Assert.Equal("User", command.Actor.Type);
        Assert.Equal(55, command.Actor.Id);
        Assert.Equal(1, fixture.TaskReads);
        Assert.All(fixture.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task LostCommittedResolutionResponseReplaysWithoutTaskLookupEvenWhenUnavailable()
    {
        var store = new Store { LoseResolutionSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        AssertResolved(store.State);
        var persistedCommand = store.State.Inbox["actions:1:44"].PayloadJson;
        fixture.TaskUnavailable = true;
        fixture.Requests.Clear();
        var result = await Run(client, store, occurredAt: Now.AddDays(-3));
        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        Assert.Equal(persistedCommand, store.State.Inbox["actions:1:44"].PayloadJson);
        AssertResolved(store.State);
        Assert.DoesNotContain(fixture.Requests, request => request.Contains("/agents/"));
        Assert.Equal(1, fixture.TaskReads);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is ResolveTaskTerminal);
        Assert.All(fixture.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task ChangedReplayCannotReusePreviouslyAuthorizedResolution()
    {
        var store = new Store { LoseResolutionSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        fixture.TaskUnavailable = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store, reason: "Changed intent"));
        Assert.Equal(1, fixture.TaskReads);
        AssertResolved(store.State);
    }

    [Theory]
    [InlineData("read", "User")]
    [InlineData("write", "Bot")]
    public async Task UnauthorizedOperatorFailsBeforeTerminalLookupOrCanonicalMutation(string permission, string actorType)
    {
        var store = new Store();
        using var fixture = new Fixture(store) { Permission = permission, ActorType = actorType };
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store, actorType: actorType));
        Assert.Equal(0, fixture.TaskReads);
        Assert.Empty(store.Saves);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Null(store.State.Issues[7].Tasks["task"].OperatorResolution);
    }

    [Theory]
    [InlineData("receipt-id")]
    [InlineData("task")]
    [InlineData("repository-generation")]
    [InlineData("issue-generation")]
    public async Task MissingCanonicalIdentityOrStaleGenerationCannotAuthorizeRecovery(string corruption)
    {
        var store = new Store();
        if (corruption == "receipt-id")
        {
            store.State.Outbox["launch"].ExternalId = null;
            store.State.Issues[7].Tasks["task"].ExternalId = null;
        }
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store,
            task: corruption == "task" ? "guessed-task" : "task",
            repositoryGeneration: corruption == "repository-generation" ? 2 : 1,
            issueGeneration: corruption == "issue-generation" ? 2 : 1));
        Assert.Equal(0, fixture.TaskReads);
        Assert.Empty(store.Saves);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.All(fixture.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Fact]
    public async Task PartialEvidenceCommitCannotBecomeOperatorResolutionWithoutNewVerifiedLookup()
    {
        var store = new Store { LoseEvidenceSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        Assert.NotNull(store.State.Issues[7].Tasks["task"].TerminalEvidence);
        Assert.Equal("terminal-for:actions:1:44", store.State.Issues[7].Tasks["task"].TerminalEvidence!.EventId);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is TaskTerminalObserved);
        Assert.Null(store.State.Issues[7].Tasks["task"].OperatorResolution);
        Assert.Equal(7, store.State.AdmittedIssue);
        fixture.TaskUnavailable = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(client, store));
        Assert.Null(store.State.Issues[7].Tasks["task"].OperatorResolution);
        Assert.Equal(TaskStatus.Unknown, store.State.Issues[7].Tasks["task"].Status);
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    [Fact]
    public async Task LostEvidenceResponseThenRetryUsesOneStableEvidenceEventBeforeResolution()
    {
        var store = new Store { LoseEvidenceSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        var evidencePayload = store.State.Inbox["terminal-for:actions:1:44"].PayloadJson;
        var result = await Run(client, store);
        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        AssertResolved(store.State);
        Assert.Equal(2, fixture.TaskReads);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is TaskTerminalObserved);
        Assert.Equal(evidencePayload, store.State.Inbox["terminal-for:actions:1:44"].PayloadJson);
        Assert.Equal("terminal-for:actions:1:44", store.State.Issues[7].Tasks["task"].OperatorResolution!.EvidenceEventId);
    }

    [Fact]
    public async Task ChangedTerminalSnapshotRequiresFreshOperatorCommandAfterInterruptedEvidence()
    {
        var store = new Store { LoseEvidenceSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        fixture.TaskState = "failed";
        fixture.SessionState = "failed";
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store));
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Null(store.State.Issues[7].Tasks["task"].OperatorResolution);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is TaskTerminalObserved);
        await Run(client, store, runId: "45");
        AssertResolved(store.State);
        Assert.Equal(2, store.State.Inbox.Values.Count(entry => entry.Event?.Data is TaskTerminalObserved));
        Assert.Equal("terminal-for:actions:1:45", store.State.Issues[7].Tasks["task"].OperatorResolution!.EvidenceEventId);
    }

    [Fact]
    public async Task ExpiredInterruptedEvidenceRequiresFreshOperatorCommand()
    {
        var store = new Store { LoseEvidenceSaveResponse = true };
        using var fixture = new Fixture(store);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<IOException>(() => Run(client, store));
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store, observedAt: Now.AddMinutes(6)));
        Assert.Null(store.State.Issues[7].Tasks["task"].OperatorResolution);
        Assert.Equal(7, store.State.AdmittedIssue);
        Assert.Single(store.State.Inbox.Values, entry => entry.Event?.Data is TaskTerminalObserved);
        await Run(client, store, runId: "45", observedAt: Now.AddMinutes(6));
        AssertResolved(store.State);
        Assert.Equal("terminal-for:actions:1:45", store.State.Issues[7].Tasks["task"].OperatorResolution!.EvidenceEventId);
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("waiting_for_user")]
    [InlineData("idle")]
    public async Task HumanAuthorizationAndPayloadProseCannotOverrideNonterminalSessions(string state)
    {
        var store = new Store();
        using var fixture = new Fixture(store) { SessionState = state };
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => Run(client, store, reason: "All completed green approved"));
        Assert.Empty(store.Saves);
        Assert.Null(store.State.Issues[7].Tasks["task"].TerminalEvidence);
        Assert.Equal(7, store.State.AdmittedIssue);
    }

    private static void AssertResolved(RepositoryState state)
    {
        var issue = state.Issues[7];
        Assert.Equal(TaskStatus.Failed, issue.Tasks["task"].Status);
        Assert.NotNull(issue.Tasks["task"].OperatorResolution);
        Assert.False(issue.Tasks["task"].LaunchConfirmed);
        Assert.Null(issue.Tasks["task"].FinalSha);
        Assert.Equal("remote-task", state.Outbox["launch"].ExternalId);
        Assert.Equal("saved-launch-digest", state.Outbox["launch"].Digest);
        Assert.Equal(DeliveryStatus.Failed, state.Outbox["launch"].Status);
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.True(issue.ReactivationRequired);
        Assert.True(issue.Paused);
        Assert.Equal("Operator hold", issue.HoldReason);
        Assert.Null(state.AdmittedIssue);
        Assert.Null(issue.Ci);
        Assert.Null(issue.Review);
        Assert.Single(issue.Tasks);
        Assert.Single(issue.Tasks["task"].CiBlockers);
        Assert.Single(issue.PendingFeedbackEventIds);
        Assert.Equal(2, issue.Round);
        Assert.DoesNotContain(state.Outbox.Values, intent => intent.Kind is EffectKind.RunCi or EffectKind.RequestReview);
    }

    private static Task<WorkerResult> Run(HttpClient client, Store store, string reason = "End verified task tracking",
        string task = "task", DateTimeOffset? occurredAt = null, string actorType = "User",
        long repositoryGeneration = 1, long issueGeneration = 1, string runId = "44", DateTimeOffset? observedAt = null)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = actorType },
            inputs = new { issue = "7", operation = "resolve-task", task, reason }
        });
        return new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(observedAt ?? Now))
            .ReconcileAsync(payload, "workflow_dispatch", runId, repositoryGeneration, issueGeneration,
                occurredAt ?? Now.AddDays(-1), [], 99, "master", 55, Approval,
                new() { MaximumIterations = 2, PollInterval = TimeSpan.Zero });
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class Store : IStateStore
    {
        public RepositoryState State { get; private set; } = Initial();
        public List<RepositoryState> Saves { get; } = [];
        public bool LoseResolutionSaveResponse { get; set; }
        public bool LoseEvidenceSaveResponse { get; set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token) =>
            Task.FromResult(new StateSnapshot(State.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(State))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            if (State.Version.ToString() != expected.Revision) return Task.FromResult(false);
            State = StateJson.Deserialize(StateJson.Serialize(next));
            Saves.Add(StateJson.Deserialize(StateJson.Serialize(next)));
            if (LoseResolutionSaveResponse && State.Inbox.Values.Any(entry => entry.Event?.Data is ResolveTaskTerminal))
            {
                LoseResolutionSaveResponse = false;
                throw new IOException("Lost response after durable operator resolution.");
            }
            if (LoseEvidenceSaveResponse && State.Inbox.Values.Any(entry => entry.Event?.Data is TaskTerminalObserved))
            {
                LoseEvidenceSaveResponse = false;
                throw new IOException("Lost response after durable API evidence.");
            }
            return Task.FromResult(true);
        }
        private static RepositoryState Initial()
        {
            var state = RepositoryState.Create("owner/repo");
            state.AdmittedIssue = 7;
            state.Issues[7] = new()
            {
                Number = 7, Phase = Phase.BLOCKED, Paused = true, ReactivationRequired = true,
                HoldReason = "Operator hold", ActiveTaskId = "task", Round = 2
            };
            state.Issues[7].Tasks["task"] = new()
            {
                Id = "task", ExternalId = "remote-task", Status = TaskStatus.Unknown, Round = 2,
                CiBlockers = [new("failure", "Existing blocker", "Prior run")]
            };
            state.Issues[7].PendingFeedbackEventIds.Add("feedback");
            state.Issues[7].Feedback["feedback"] = new("feedback", "Preserve this request",
                new("maintainer", ActorKind.Human, 55), Now, 1, null, 2, null);
            state.Outbox["launch"] = new()
            {
                Id = "launch", Digest = "saved-launch-digest", IssueNumber = 7, Generation = 1,
                TaskId = "task", Round = 2, Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Unknown,
                ExternalId = "remote-task", CreatedAt = Now.AddDays(-2)
            };
            return state;
        }
    }
    private sealed class Fixture(Store store) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public int TaskReads { get; private set; }
        public bool TaskUnavailable { get; set; }
        public string Permission { get; set; } = "write";
        public string ActorType { get; set; } = "User";
        public string SessionState { get; set; } = "completed";
        public string TaskState { get; set; } = "completed";
        public HttpClient Client() => new(this) { BaseAddress = new("https://api.github.com/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("No writes expected in terminal recovery fixture.");
            object response;
            switch (path)
            {
                case "/repos/owner/repo": response = new { id = 1, full_name = "owner/repo" }; break;
                case "/repos/owner/repo/actions/runs/44":
                case "/repos/owner/repo/actions/runs/45":
                    response = new { @event = "workflow_dispatch", path = ".github/workflows/agent-state-machine.yml", actor = new { id = 55 } }; break;
                case "/users/maintainer": response = new { id = 55, type = ActorType }; break;
                case "/repos/owner/repo/collaborators/maintainer/permission":
                    response = new { permission = Permission, user = new { id = 55 } }; break;
                case "/repos/owner/repo/issues/7":
                    response = new { id = 70, number = 7, state = "open", labels = new[] { new { name = "agent:blocked" } } }; break;
                case "/repos/owner/repo/issues/7/labels":
                    response = new[] { new { name = "agent:blocked" } }; break;
                case "/repos/owner/repo/issues/7/comments":
                    response = new[] { new { id = 101, body = StatusProjection.Render(store.State.Issues[7]),
                        updated_at = Now, user = new { id = 99, type = "Bot", login = "projection" } } }; break;
                case "/agents/repos/owner/repo/tasks/remote-task":
                    TaskReads++;
                    if (TaskUnavailable) throw new HttpRequestException("Task API unavailable after prior commit.");
                    response = new
                    {
                        id = "remote-task", repository = new { id = 1 }, creator = new { id = 55 },
                        state = TaskState, updated_at = Now.AddMinutes(-1), session_count = 1,
                        sessions = new[] { new { id = "session", task_id = "remote-task",
                            repository = new { id = 1 }, state = SessionState, completed_at = Now.AddMinutes(-1) } }
                    };
                    break;
                default: throw new InvalidOperationException($"Unexpected lookup: {path}");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") });
        }
    }
}
