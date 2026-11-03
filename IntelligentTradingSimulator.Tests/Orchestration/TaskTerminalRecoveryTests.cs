using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class TaskTerminalRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T01:00:00Z");
    private static readonly OrchestrationReducer Reducer = new(new Allow());

    [Theory]
    [InlineData("completed", false)]
    [InlineData("failed", false)]
    [InlineData("cancelled", false)]
    [InlineData("timed_out", false)]
    [InlineData("completed", true)]
    public async Task SavedTaskEndedEvidenceRequiresExplicitOperatorAndNeverAuthorizesCi(string remoteState, bool confirmed)
    {
        var state = State(confirmed);
        using var fixture = new ApiFixture(remoteState);
        using var client = fixture.Client();
        var message = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        var evidenced = Reducer.Apply(state, message);
        Assert.Equal(7, evidenced.AdmittedIssue);
        Assert.Equal(confirmed ? TaskStatus.Running : TaskStatus.Unknown, evidenced.Issues[7].Tasks["task"].Status);
        Assert.Equal(confirmed ? DeliveryStatus.Succeeded : DeliveryStatus.Unknown, evidenced.Outbox["launch"].Status);
        var resolution = Resolution(message.Id);
        var ended = Reducer.Apply(StateJson.Deserialize(StateJson.Serialize(evidenced)), resolution);
        var task = ended.Issues[7].Tasks["task"];
        Assert.Equal(TaskStatus.Failed, task.Status);
        Assert.Equal(confirmed, task.LaunchConfirmed);
        Assert.Null(task.FinalSha);
        Assert.Equal("remote-task", task.ExternalId);
        Assert.NotNull(task.OperatorResolution);
        Assert.Equal("maintainer", task.OperatorResolution.Author.Login);
        Assert.Equal("Existing operator hold", ended.Issues[7].HoldReason);
        Assert.True(ended.Issues[7].Paused);
        Assert.True(ended.Issues[7].ReactivationRequired);
        Assert.False(ended.Issues[7].ActiveAuthorized);
        Assert.Equal(4, ended.Issues[7].Round);
        Assert.Single(task.CiBlockers);
        Assert.Single(task.ReviewBlockers);
        Assert.Single(ended.Issues[7].PendingFeedbackEventIds);
        Assert.Null(ended.AdmittedIssue);
        Assert.Equal(StateJson.Serialize(ended), StateJson.Serialize(Reducer.Apply(ended, resolution)));
        var live = new LiveObservation { ObservedAt = Now };
        live.Issues[7] = new() { Ready = true };
        var decision = Reducer.Reconcile(ended, live);
        Assert.Null(decision.State.Issues[7].Ci);
        Assert.Null(decision.State.Issues[7].Review);
        Assert.Single(decision.State.Issues[7].Tasks);
        Assert.DoesNotContain(decision.PendingEffects, effect => effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
        Assert.All(fixture.Requests, request => Assert.StartsWith("GET ", request));
        Assert.Equal(2, fixture.Requests.Count);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    [InlineData("idle")]
    [InlineData("waiting_for_user")]
    [InlineData("unsupported")]
    public async Task NonendedTaskOrSessionStatesCannotSupplyEvidence(string remoteState)
    {
        var state = State();
        using var fixture = new ApiFixture(remoteState);
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default));
        fixture.Response["state"] = "completed";
        ((JsonObject)fixture.Response["sessions"]![0]!)["state"] = remoteState;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default));
        Assert.Equal(7, state.AdmittedIssue);
        Assert.All(fixture.Requests, request => Assert.StartsWith("GET ", request));
    }

    [Theory]
    [InlineData("task-id")]
    [InlineData("repository")]
    [InlineData("creator")]
    [InlineData("missing-sessions")]
    [InlineData("empty-sessions")]
    [InlineData("missing-count")]
    [InlineData("count")]
    [InlineData("missing-session-id")]
    [InlineData("duplicate-session")]
    [InlineData("missing-session-task")]
    [InlineData("session-task")]
    [InlineData("missing-session-repository")]
    [InlineData("session-repository")]
    [InlineData("missing-completed-at")]
    [InlineData("future-completed-at")]
    [InlineData("missing-updated-at")]
    [InlineData("future-updated-at")]
    [InlineData("contradictory-state")]
    public async Task IncompleteOrContradictorySnapshotRetainsAdmission(string corruption)
    {
        var state = State();
        using var fixture = new ApiFixture();
        var root = fixture.Response;
        var session = (JsonObject)root["sessions"]![0]!;
        switch (corruption)
        {
            case "task-id": root["id"] = "other"; break;
            case "repository": root["repository"]!["id"] = 2; break;
            case "creator": root["creator"]!["id"] = 66; break;
            case "missing-sessions": root.Remove("sessions"); break;
            case "empty-sessions": root["sessions"] = new JsonArray(); break;
            case "missing-count": root.Remove("session_count"); break;
            case "count": root["session_count"] = 2; break;
            case "missing-session-id": session.Remove("id"); break;
            case "duplicate-session": ((JsonArray)root["sessions"]!).Add(session.DeepClone()); root["session_count"] = 2; break;
            case "missing-session-task": session.Remove("task_id"); break;
            case "session-task": session["task_id"] = "other"; break;
            case "missing-session-repository": session.Remove("repository"); break;
            case "session-repository": session["repository"]!["id"] = 2; break;
            case "missing-completed-at": session.Remove("completed_at"); break;
            case "future-completed-at": session["completed_at"] = Now.AddMinutes(1).ToString("O"); break;
            case "missing-updated-at": root.Remove("updated_at"); break;
            case "future-updated-at": root["updated_at"] = Now.AddMinutes(1).ToString("O"); break;
            case "contradictory-state": session["state"] = "failed"; break;
        }
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default));
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(TaskStatus.Unknown, state.Issues[7].Tasks["task"].Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostLaunchWithoutSavedIdNeverSearchesOrGuesses(bool missingTaskId)
    {
        var state = State();
        if (missingTaskId) state.Issues[7].ActiveTaskId = null;
        else
        {
            state.Outbox["launch"].ExternalId = null;
            state.Issues[7].Tasks["task"].ExternalId = null;
        }
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default));
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task UnauthorizedOperatorOrServiceImpersonationCannotResolve()
    {
        var state = State();
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var message = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        state = Reducer.Apply(state, message);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() =>
            new OrchestrationReducer().Apply(state, Resolution(message.Id))));
        Assert.Throws<UnauthorizedAccessException>(() => Reducer.Apply(state,
            Resolution(message.Id) with { Actor = new("github-task-api", ActorKind.TaskService) }));
        Assert.Throws<UnauthorizedAccessException>(() => Reducer.Apply(State(),
            message with { Actor = new("maintainer", ActorKind.Human) }));
        Assert.Equal(7, state.AdmittedIssue);
    }

    [Fact]
    public async Task MissingExpiredOrMismatchedDurableEvidenceCannotResolve()
    {
        var state = State();
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Resolution("absent")));
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var message = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        state = Reducer.Apply(state, message);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Resolution("other")));
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state,
            Resolution(message.Id) with { OccurredAt = Now.AddMinutes(6) }));
        state.Inbox.Remove(message.Id);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Resolution(message.Id)));
    }

    [Fact]
    public async Task ChangedEvidenceOrMissingOperatorIdentityCannotResolve()
    {
        var state = State();
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var message = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        state = Reducer.Apply(state, message);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state,
            Resolution(message.Id) with { Actor = new("maintainer", ActorKind.Human) }));
        var record = state.Issues[7].Tasks["task"].TerminalEvidence!;
        state.Issues[7].Tasks["task"].TerminalEvidence = record with
        {
            Evidence = record.Evidence with { CreatorId = 66 }
        };
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(state, Resolution(message.Id)));
        Assert.Equal(7, state.AdmittedIssue);
    }

    [Fact]
    public async Task ResolvedIssueRemainsHeldWhileAnotherReadyIssueCanTakeAdmission()
    {
        var state = State();
        state.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var evidence = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        var ended = Reducer.Apply(Reducer.Apply(state, evidence), Resolution(evidence.Id));
        var live = new LiveObservation { ObservedAt = Now };
        live.Issues[7] = new() { Ready = true };
        live.Issues[8] = new() { Ready = true };
        var next = Reducer.Reconcile(ended, live);
        Assert.Equal(8, next.State.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, next.State.Issues[7].Phase);
        Assert.True(next.State.Issues[7].ReactivationRequired);
        Assert.DoesNotContain(next.PendingEffects, effect => effect.IssueNumber == 7 &&
            effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
        Assert.Contains(next.PendingEffects, effect => effect.IssueNumber == 8 && effect.Kind == EffectKind.LaunchTask);
    }

    [Theory]
    [InlineData("ci")]
    [InlineData("review")]
    [InlineData("unknown-effect")]
    [InlineData("other-task")]
    public async Task ResolutionNeverReleasesAdmissionWithOtherUnfinishedOperation(string other)
    {
        var state = State();
        switch (other)
        {
            case "ci": state.Issues[7].Ci = new() { RunId = "run" }; break;
            case "review": state.Issues[7].Review = new() { ReviewId = "review" }; break;
            case "unknown-effect": state.Outbox["other"] = new() { Id = "other", IssueNumber = 7, Kind = EffectKind.ProjectLabels, Status = DeliveryStatus.Unknown }; break;
            case "other-task": state.Issues[7].Tasks["other"] = new() { Id = "other", Status = TaskStatus.Running }; break;
        }
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var evidence = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        var ended = Reducer.Apply(Reducer.Apply(state, evidence), Resolution(evidence.Id));
        Assert.Equal(7, ended.AdmittedIssue);
        Assert.Throws<InvalidOperationException>(() => Reducer.Apply(ended,
            Resolution(evidence.Id) with { Id = "reactivate", Data = new Reactivate("Human wants retry") }));
    }

    [Fact]
    public async Task LostResolutionResponseAndPersistenceReplayRetainAuditAndLaunchReceipt()
    {
        var state = State();
        using var fixture = new ApiFixture();
        using var client = fixture.Client();
        var evidence = await Adapter(client).ReadTerminalEventAsync(state, state.Issues[7], Now, default);
        var before = Reducer.Apply(state, evidence);
        var resolution = Resolution(evidence.Id);
        var unsaved = Reducer.Apply(before, resolution);
        var replayBeforeSave = Reducer.Apply(StateJson.Deserialize(StateJson.Serialize(before)), resolution);
        Assert.Equal(StateJson.Serialize(unsaved), StateJson.Serialize(replayBeforeSave));
        var saved = StateJson.Deserialize(StateJson.Serialize(unsaved));
        Assert.Equal(StateJson.Serialize(saved), StateJson.Serialize(Reducer.Apply(saved, resolution)));
        Assert.Equal("receipt-digest", saved.Outbox["launch"].Digest);
        Assert.Equal("remote-task", saved.Outbox["launch"].ExternalId);
        Assert.Single(saved.Issues[7].Tasks);
        Assert.Throws<InvalidOperationException>(() => Reducer.RecordOutcome(saved, "launch",
            new(DeliveryStatus.Succeeded, "remote-task"), Now));
    }

    private static GitHubTaskAdapter Adapter(HttpClient client) =>
        new(new(client, "owner/repo", 1), new UnusedStore(), "master", 55);
    private static OrchestrationEvent Resolution(string evidenceId) =>
        new("operator-resolution", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human, 55, Type: "User"),
            Now, new ResolveTaskTerminal("task", evidenceId, "Verified ended sessions; retain hold"));

    private static RepositoryState State(bool confirmed = false)
    {
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        var issue = new IssueState
        {
            Number = 7, Phase = Phase.BLOCKED, ActiveTaskId = "task", Paused = true,
            HoldReason = "Existing operator hold", ReactivationRequired = true, Round = 4
        };
        issue.Tasks["task"] = new()
        {
            Id = "task", ExternalId = "remote-task", Round = 4, LaunchConfirmed = confirmed,
            Status = confirmed ? TaskStatus.Running : TaskStatus.Unknown,
            FeedbackEventIds = ["feedback"],
            CiBlockers = [new("ci", "CI blocker", "evidence")],
            ReviewBlockers = [new("review", "Review blocker", "evidence")]
        };
        issue.PendingFeedbackEventIds.Add("feedback");
        issue.Feedback["feedback"] = new("feedback", "Keep feedback", new("maintainer", ActorKind.Human),
            Now, 1, null, 4, null);
        state.Issues[7] = issue;
        state.Outbox["launch"] = new()
        {
            Id = "launch", Digest = "receipt-digest", IssueNumber = 7, Generation = 1, Round = 4,
            Kind = EffectKind.LaunchTask, TaskId = "task", ExternalId = "remote-task",
            Status = confirmed ? DeliveryStatus.Succeeded : DeliveryStatus.Unknown, CreatedAt = Now.AddHours(-1)
        };
        return state;
    }

    private sealed class Allow : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => true;
    }
    private sealed class UnusedStore : IStateStore
    {
        public Task<StateSnapshot> LoadAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class ApiFixture : HttpMessageHandler
    {
        public JsonObject Response { get; }
        public List<string> Requests { get; } = [];
        public ApiFixture(string state = "completed")
        {
            Response = new()
            {
                ["id"] = "remote-task", ["repository"] = new JsonObject { ["id"] = 1 },
                ["creator"] = new JsonObject { ["id"] = 55 }, ["state"] = state,
                ["updated_at"] = Now.ToString("O"), ["session_count"] = 1,
                ["sessions"] = new JsonArray(new JsonObject
                {
                    ["id"] = "session", ["task_id"] = "remote-task",
                    ["repository"] = new JsonObject { ["id"] = 1 }, ["state"] = state,
                    ["completed_at"] = Now.ToString("O")
                })
            };
        }
        public HttpClient Client() => new(this) { BaseAddress = new Uri("https://api.github.com/") };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + path);
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("Recovery must remain lookup-only.");
            var body = path switch
            {
                "/repos/owner/repo" => """{"id":1,"full_name":"owner/repo"}""",
                "/agents/repos/owner/repo/tasks/remote-task" => Response.ToJsonString(),
                _ => throw new InvalidOperationException("No task search or guessed lookup is allowed.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
