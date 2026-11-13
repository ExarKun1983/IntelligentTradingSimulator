using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubCiIntegrationTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly string Definition = new('b', 40);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly GitHubCiRoute Route = new(10, "refs/tags/ci-trusted", Definition);
    private static readonly GitHubCiApproval Approved = new(true, true, true, true);
    private static GitHubCiAdapter CreateAdapter(HttpClient client, Store store, GitHubCiRoute? route = null) =>
        new(new(client, "owner/repo", 1), store, route ?? Route, new Clock(), Approved, new CandidateValidator());

    [Fact]
    public async Task FeedbackCommittedAfterBoundaryAndDuringFinalLiveCheckDefersDispatch()
    {
        var store = new Store(State());
        var api = new Api();
        api.AfterPullRead = () =>
        {
            if (store.Value.Issues[7].Ci!.DispatchBoundary is not null)
                store.Value.Issues[7].PendingFeedbackEventIds.Add("authorized-feedback");
        };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.NotNull(store.Value.Issues[7].Ci!.DispatchBoundary);
        Assert.Null(store.Value.Issues[7].Ci!.DispatchBoundary!.RunId);
        Assert.Equal(0, api.Posts);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.Value.Outbox["ci"], default)).Status);
        Assert.Equal(0, api.Posts);
    }

    [Theory]
    [InlineData(GitHubIsolatedStatusTestSettings.CiWorkflowPath)]
    [InlineData(GitHubPocSettings.CiWorkflowPath)]
    public async Task IsolatedCiRoutePersistsItsOwnWorkflowPathThroughRunEvidence(string path)
    {
        var store = new Store(State());
        var api = new Api { WorkflowPath = path, BaseBranch = path == GitHubPocSettings.CiWorkflowPath
            ? GitHubPocSettings.BaseBranch : GitHubIsolatedStatusTestSettings.BaseBranch };
        using var client = api.Client();
        var route = Route with { WorkflowPath = path, Ref = path == GitHubPocSettings.CiWorkflowPath
            ? GitHubPocSettings.DefinitionReference : Route.Ref };
        var adapter = CreateAdapter(client, store, route);

        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal(path, store.Value.Issues[7].Ci!.DispatchBoundary!.WorkflowPath);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        Assert.NotNull(await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default));
    }

    [Theory]
    [InlineData("other", true)]
    [InlineData(GitHubPocSettings.BaseBranch, false)]
    public async Task PocCiRejectsWrongBaseOrNonDraftBeforeDispatch(string baseBranch, bool draft)
    {
        var store = new Store(State());
        var api = new Api { WorkflowPath = GitHubPocSettings.CiWorkflowPath, BaseBranch = baseBranch, Draft = draft };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store, Route with { WorkflowPath = GitHubPocSettings.CiWorkflowPath,
            Ref = GitHubPocSettings.DefinitionReference });
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Equal(0, api.Posts);
    }

    [Theory]
    [InlineData("refs/tags/trusted-ci")]
    [InlineData("refs/heads/other")]
    public async Task PocCiRejectsDefinitionsOutsideMaster(string reference)
    {
        var store = new Store(State());
        var api = new Api { WorkflowPath = GitHubPocSettings.CiWorkflowPath };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store, Route with
        {
            WorkflowPath = GitHubPocSettings.CiWorkflowPath, Ref = reference
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("ci-trusted")]
    [InlineData("refs/heads/ci-trusted")]
    [InlineData("refs/tags/")]
    public async Task MutableOrAmbiguousDefinitionRefIsRejectedBeforeIo(string reference)
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = CreateAdapter(client, store, Route with { Ref = reference });
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("build-failure")]
    [InlineData("test-failure")]
    public async Task SharedWorkerDispatchesOnceAndConsumesResultBeforeReviewOrRepair(string result)
    {
        var store = new Store(State());
        store.Value.Outbox["ci"].Status = DeliveryStatus.Pending;
        var api = new Api { Result = result };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var worker = new OrchestrationWorker(new(new Allow()), new Clock());
        var outcome = await worker.RunAsync(store, new Events(adapter), new Observations(), new Effects(adapter),
            new(false, true, true, true), new() { MaximumIterations = 2, PollInterval = TimeSpan.Zero });
        Assert.Equal(1, api.Posts);
        Assert.NotNull(store.Value.Issues[7].Ci!.Green);
        if (result == "success")
            Assert.NotNull(store.Value.Issues[7].Review);
        else
            Assert.Equal(TaskKind.CiRepair, store.Value.Issues[7].Tasks.Values.Single(task => task.Kind == TaskKind.CiRepair).Kind);
        await worker.RunAsync(store, new Events(adapter), new Observations(), new Effects(adapter),
            new(false, true, true, true), new() { MaximumIterations = 2, PollInterval = TimeSpan.Zero });
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task CandidateChangePreventsCiDispatchAndResultAcceptance()
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var validator = new CandidateValidator { Reject = true };
        var adapter = new GitHubCiAdapter(new(client, "owner/repo", 1), store, Route, new Clock(), Approved, validator);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Equal(0, api.Posts);

        var issue = store.Value.Issues[7];
        issue.Ci!.RunId = "90";
        issue.Ci.DispatchBoundary = new(10, Definition, Route.Ref, "correlation", Now, 90);
        store.Value.Outbox["ci"].Status = DeliveryStatus.Succeeded;
        store.Value.Outbox["ci"].ExternalId = "90";
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReadEventAsync(store.Value, issue, default));
        Assert.Null(issue.Ci.Green);
    }

    [Fact]
    public async Task InterruptBeforeBoundaryWriteNeverDispatches()
    {
        var store = new Store(State()) { Fail = true };
        var api = new Api();
        using var client = api.Client();
        await Assert.ThrowsAsync<IOException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Equal(0, api.Posts);
        Assert.Null(store.Value.Issues[7].Ci!.DispatchBoundary);
    }

    [Fact]
    public async Task ConcurrentExecutorsCannotDispatchTheSameIntentTwice()
    {
        var store = new Store(State());
        var api = new Api { SynchronizePreflight = true };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        async Task<bool> Execute()
        {
            try { await adapter.ExecuteAsync(store.Value.Outbox["ci"], default); return true; }
            catch (InvalidDataException) { return false; }
        }
        var outcomes = await Task.WhenAll(Execute(), Execute());
        Assert.Single(outcomes, value => value);
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task DisabledOrUnverifiedDispatchPerformsNoIo(bool activation, bool credential, bool trust, bool cost)
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = new GitHubCiAdapter(new(client, "owner/repo", 1), store, Route, new Clock(),
            new(activation, credential, trust, cost));
        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("unconfirmed")]
    [InlineData("feedback")]
    [InlineData("hold")]
    [InlineData("task-sha")]
    [InlineData("task-round")]
    [InlineData("admission")]
    [InlineData("intent")]
    public async Task CodingOrInvalidCanonicalStateCannotDispatch(string corruption)
    {
        var store = new Store(State());
        var issue = store.Value.Issues[7];
        switch (corruption)
        {
            case "running": issue.Tasks["task"].Status = TaskStatus.Running; break;
            case "unconfirmed": issue.Tasks["task"].LaunchConfirmed = false; break;
            case "feedback": issue.PendingFeedbackEventIds.Add("human"); break;
            case "hold": issue.ReactivationRequired = true; break;
            case "task-sha": issue.Tasks["task"].FinalSha = Definition; break;
            case "task-round": issue.Tasks["task"].Round++; break;
            case "admission": store.Value.AdmittedIssue = null; break;
            case "intent": store.Value.Outbox["ci"].Kind = EffectKind.RequestReview; break;
        }
        var api = new Api();
        using var client = api.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Empty(api.Requests);
    }

    [Theory]
    [InlineData("workflow-path")]
    [InlineData("workflow-disabled")]
    [InlineData("ref-moved")]
    [InlineData("head-changed")]
    [InlineData("repository")]
    public async Task LiveRouteAndHeadPreflightStopDispatch(string corruption)
    {
        var store = new Store(State());
        var api = new Api { Corruption = corruption };
        using var client = api.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateAdapter(client, store)
                .ExecuteAsync(store.Value.Outbox["ci"], default));
        Assert.Equal(0, api.Posts);
    }

    [Fact]
    public async Task DispatchCapturesProviderRunIdAndLostOutcomeRecoversSameRunWithoutRedispatch()
    {
        var store = new Store(State());
        var api = new Api();
        api.BeforePost = () => Assert.Null(store.Value.Issues[7].Ci!.DispatchBoundary!.RunId);
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        Assert.Equal("90", outcome.ExternalId);
        Assert.Equal(90, store.Value.Issues[7].Ci!.DispatchBoundary!.RunId);
        using var body = JsonDocument.Parse(api.Body);
        Assert.Equal("refs/tags/ci-trusted", body.RootElement.GetProperty("ref").GetString());
        Assert.Equal(Sha, body.RootElement.GetProperty("inputs").GetProperty("sha").GetString());
        Assert.Equal(1, api.Posts);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", new(DeliveryStatus.Unknown), Now);
        var recovered = await adapter.ReconcileAsync(store.Value.Outbox["ci"], default);
        Assert.Equal(outcome, recovered);
        Assert.Equal(outcome, await adapter.ReconcileAsync(store.Value.Outbox["ci"], default));
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("save-run")]
    [InlineData("receipt")]
    public async Task AmbiguousDispatchStaysUnknownWithoutSearchingOrLaunchingAgain(string corruption)
    {
        var store = new Store(State());
        var api = new Api { Corruption = corruption };
        if (corruption == "save-run") api.BeforePost = () => store.Fail = true;
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.ExecuteAsync(store.Value.Outbox["ci"], default));
        store.Fail = false;
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", new(DeliveryStatus.Unknown), Now);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.Value.Outbox["ci"], default)).Status);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.Value.Outbox["ci"], default)).Status);
        Assert.Equal(1, api.Posts);
        Assert.Equal(7, store.Value.AdmittedIssue);
        Assert.True(store.Value.Issues[7].ReactivationRequired);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("build-failure")]
    [InlineData("test-failure")]
    public async Task ExactRunAndVerifiedTestStepsAuthorizeOnlyEligibleCiResult(string result)
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        api.Result = result;
        var message = await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default);
        var ci = Assert.IsType<CiResult>(message!.Data);
        Assert.Equal(result == "success", ci.Green);
        Assert.Equal(Sha, ci.Sha);
        Assert.Equal("task", ci.TaskId);
        var reducer = new OrchestrationReducer(new Allow());
        store.Value = reducer.Apply(store.Value, message);
        var serialized = StateJson.Serialize(store.Value);
        store.Value = reducer.Apply(store.Value, message);
        Assert.Equal(serialized, StateJson.Serialize(store.Value));
        Assert.Equal(result == "success" ? Phase.REVIEW_PENDING : Phase.FIX_PENDING, store.Value.Issues[7].Phase);
        Assert.Null(await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default));
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData("run-id")]
    [InlineData("run-path")]
    [InlineData("run-sha")]
    [InlineData("run-event")]
    [InlineData("run-attempt")]
    [InlineData("run-title")]
    [InlineData("run-repository")]
    public async Task WrongRunOrDefinitionCannotSatisfyCurrentTask(string corruption)
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        api.Corruption = corruption;
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default));
        Assert.Null(store.Value.Issues[7].Ci!.Green);
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("verify")]
    [InlineData("restore")]
    [InlineData("job-sha")]
    [InlineData("job-attempt")]
    [InlineData("job-name")]
    [InlineData("missing-job")]
    [InlineData("duplicate-job")]
    public async Task TechnicalOrIncompleteEvidenceCreatesDurableStopNotCodingRepair(string corruption)
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        api.Corruption = corruption;
        var message = await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default);
        Assert.IsType<CiStopped>(message!.Data);
        store.Value = new OrchestrationReducer(new Allow()).Apply(store.Value, message);
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.Null(store.Value.Issues[7].Ci!.Green);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task ChangedHeadReconcilesTerminalRunWithoutReplacementDispatch()
    {
        var store = new Store(State());
        var api = new Api();
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        api.Corruption = "head-changed";
        var message = (await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default))!;
        Assert.IsType<CiStopped>(message.Data);
        store.Value = new OrchestrationReducer(new Allow()).Apply(store.Value, message);
        Assert.Null(store.Value.AdmittedIssue);
        Assert.NotNull(store.Value.Issues[7].Ci!.TerminalStopReason);
        store.Value = new OrchestrationReducer(new Allow()).Apply(store.Value,
            new("push", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human), Now, new HeadChanged(17, Definition)));
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task VerifiedTerminalStopReleasesAdmissionButOnlyExplicitReactivationClearsHold()
    {
        var store = new Store(State());
        var api = new Api { Corruption = "cancelled" };
        using var client = api.Client();
        var adapter = CreateAdapter(client, store);
        var outcome = await adapter.ExecuteAsync(store.Value.Outbox["ci"], default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, "ci", outcome, Now);
        var message = (await adapter.ReadEventAsync(store.Value, store.Value.Issues[7], default))!;
        var reducer = new OrchestrationReducer(new Allow());
        var stopped = reducer.Apply(store.Value, message);
        Assert.Null(stopped.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, stopped.Issues[7].Phase);
        Assert.Null(stopped.Issues[7].Ci!.Green);
        Assert.Equal("90", stopped.Issues[7].Ci!.RunId);
        Assert.NotNull(stopped.Issues[7].Ci!.TerminalStopReason);
        Assert.Equal(StateJson.Serialize(stopped), StateJson.Serialize(reducer.Apply(stopped, message)));
        Assert.Null(await adapter.ReadEventAsync(stopped, stopped.Issues[7], default));
        var ready = reducer.Apply(stopped, new("reactivate", "owner/repo", 7, 1, 1,
            new("human", ActorKind.Human), Now, new Reactivate("Verified terminal run; restart explicitly.")));
        Assert.Equal(Phase.READY, ready.Issues[7].Phase);
        Assert.Equal(2, ready.Issues[7].Generation);
        Assert.Null(ready.Issues[7].Ci);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public void WorkflowRouteIsIsolatedAndLiteralDisabled()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "IntelligentTradingSimulator.sln")))
            directory = directory.Parent;
        var workflow = File.ReadAllText(Path.Combine(directory!.FullName, GitHubCiAdapter.WorkflowPath));
        Assert.Contains("vars.ORCHESTRATION_ACTIVATION_APPROVED == 'true'", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_ISSUE == '261'", workflow);
        Assert.Contains("vars.ORCHESTRATION_RETIRED_TEST_ISSUE == ''", workflow);
        Assert.Contains("vars.ORCHESTRATION_TEST_MAX_REPAIR_TASKS == '1'", workflow);
        Assert.Contains("github.ref == vars.ORCHESTRATION_CI_REF", workflow);
        Assert.Contains("startsWith(github.ref, 'refs/tags/')", workflow);
        Assert.Contains("ref: ${{ inputs.sha }}", workflow);
        Assert.Contains("persist-credentials: false", workflow);
        Assert.Contains("git rev-parse HEAD", workflow);
        Assert.Contains("contents: read", workflow);
        Assert.DoesNotContain("secrets.", workflow);
        Assert.DoesNotContain("pull_request:", workflow);
        Assert.DoesNotContain("push:", workflow);
        Assert.DoesNotContain("checks: write", workflow);
    }

    private static RepositoryState State()
    {
        // Completed task/SHA supplied by independent offline fixture only; no provider attestation is implied.
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        state.Issues[7] = new() { Number = 7, Phase = Phase.CI_PENDING, ActiveAuthorized = true,
            PullRequest = 17, HeadSha = Sha, ActiveTaskId = "task", Round = 1,
            Tasks = new() { ["task"] = new() { Id = "task", Status = TaskStatus.Completed, LaunchConfirmed = true,
                ExternalId = "remote-task", Round = 1, FinalSha = Sha,
                CandidateShaEvidence = new(TaskTerminalRecovery.CandidateShaEvidenceVersion,
                    TaskTerminalRecovery.CandidateShaPolicy, "task", "remote-task", ["session"], 17, Sha) } },
            Ci = new() { IntentId = "ci", TaskId = "task", Round = 1, Sha = Sha } };
        state.Outbox["ci"] = new() { Id = "ci", Digest = "digest", Kind = EffectKind.RunCi,
            Status = DeliveryStatus.Executing, IssueNumber = 7, Generation = 1, TaskId = "task", Round = 1, Sha = Sha };
        return state;
    }
    private sealed class Allow : IAuthorizationPolicy { public bool IsAuthorized(OrchestrationEvent message) => true; }
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
    private sealed class Events(GitHubCiAdapter adapter) : IWorkerEventSource
    {
        public async Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token)
        {
            var message = await adapter.ReadEventAsync(state, state.Issues[7], token);
            return new(message is not null && !state.Inbox.ContainsKey(message.Id) ? [message] : []);
        }
    }
    private sealed class Observations : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) =>
            Task.FromResult(new LiveObservation { ObservedAt = Now,
                Issues = new() { [7] = new() { PullRequest = 17, HeadSha = Sha, Ready = true,
                    Labels = OrchestrationReducer.ProjectLabels(state.Issues[7]).ToList() } } });
    }
    private sealed class Effects(GitHubCiAdapter adapter) : IEffectAdapter
    {
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) =>
            intent.Kind == EffectKind.RunCi ? adapter.ExecuteAsync(intent, token) :
            intent.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus
                ? Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "offline-projection"))
                : throw new InvalidOperationException("No live review or repair effects.");
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) => adapter.ReconcileAsync(intent, token);
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store(RepositoryState state) : IStateStore
    {
        private readonly object gate = new();
        public RepositoryState Value { get; set; } = state;
        public bool Fail { get; set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token)
        {
            lock (gate) return Task.FromResult(new StateSnapshot(Value.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(Value))));
        }
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            lock (gate)
            {
                if (Fail) throw new IOException("Injected CI write failure");
                if (expected.Revision != Value.Version.ToString()) return Task.FromResult(false);
                Value = StateJson.Deserialize(StateJson.Serialize(next));
                return Task.FromResult(true);
            }
        }
    }
    private sealed class Api : HttpMessageHandler
    {
        public string? Corruption { get; set; }
        public string Result { get; set; } = "success";
        public string WorkflowPath { get; set; } = GitHubCiAdapter.WorkflowPath;
        public string BaseBranch { get; init; } = GitHubIsolatedStatusTestSettings.BaseBranch;
        public bool Draft { get; init; } = true;
        public int Posts { get; private set; }
        public string Body { get; private set; } = "";
        public List<string> Requests { get; } = [];
        public Action? BeforePost { get; set; }
        public Action? AfterPullRead { get; set; }
        public bool SynchronizePreflight { get; init; }
        private readonly TaskCompletionSource preflights = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public HttpClient Client() => new(this) { BaseAddress = new("https://api.github.com/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.Method + " " + request.RequestUri.PathAndQuery);
            object result;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/repos/owner/repo/actions/workflows/10/dispatches", path);
                BeforePost?.Invoke();
                Posts++;
                Body = await request.Content!.ReadAsStringAsync(token);
                if (Corruption == "lost") throw new HttpRequestException("Injected dispatch response loss");
                result = new { workflow_run_id = 90, run_url = Corruption == "receipt" ? "wrong" : RunUrl };
            }
            else if (path == "/repos/owner/repo") result = new { id = 1, full_name = "owner/repo" };
            else if (path.EndsWith("/workflows/10")) result = new { id = 10,
                path = Corruption == "workflow-path" ? ".github/workflows/ci.yml" : WorkflowPath,
                state = Corruption == "workflow-disabled" ? "disabled_manually" : "active" };
            else if (path.Contains("/commits/")) result = new { sha = Corruption == "ref-moved" ? Sha : Definition };
            else if (path.EndsWith("/issues/7")) result = new { number = 7, state = "open" };
            else if (path.EndsWith("/pulls/17"))
            {
                if (SynchronizePreflight)
                {
                    if (Interlocked.Increment(ref reads) == 2) preflights.TrySetResult();
                    await preflights.Task.WaitAsync(token);
                }
                result = new { number = 17, state = "open", draft = Draft,
                    head = new { sha = Corruption == "head-changed" ? Definition : Sha,
                        repo = new { id = Corruption == "repository" ? 2 : 1 } },
                    @base = new { repo = new { id = 1 }, @ref = BaseBranch } };
                AfterPullRead?.Invoke();
            }
            else if (path.EndsWith("/runs/90"))
            {
                using var body = JsonDocument.Parse(Body);
                result = new { id = Corruption == "run-id" ? 91 : 90, workflow_id = 10,
                    path = Corruption == "run-path" ? "other.yml" : WorkflowPath,
                    @event = Corruption == "run-event" ? "push" : "workflow_dispatch",
                    head_sha = Corruption == "run-sha" ? Sha : Definition, run_attempt = Corruption == "run-attempt" ? 2 : 1,
                    display_title = Corruption == "run-title" ? "other" : body.RootElement.GetProperty("inputs").GetProperty("correlation").GetString(),
                    repository = new { id = Corruption == "run-repository" ? 2 : 1 }, status = "completed",
                    conclusion = Corruption == "cancelled" ? "cancelled" : Result == "success" ? "success" : "failure", updated_at = Now };
            }
            else if (path.EndsWith("/jobs"))
            {
                var steps = new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" }
                    .Select(name => new { name, status = "completed",
                        conclusion = name == "Verify tested SHA" && Corruption == "verify" ||
                            name == "Restore dependencies" && Corruption == "restore" ||
                            name == "Build solution" && Result == "build-failure" ||
                            name == "Run tests" && Result == "test-failure" ? "failure" : "success" }).ToArray();
                var job = new { id = 100, run_id = 90, run_attempt = Corruption == "job-attempt" ? 2 : 1,
                    head_sha = Corruption == "job-sha" ? Sha : Definition, name = Corruption == "job-name" ? "Test other" : "Test " + Sha,
                    status = "completed", conclusion = Result == "success" ? "success" : "failure", steps };
                result = new { jobs = Corruption == "missing-job" ? [] : Corruption == "duplicate-job" ? new[] { job, job } : new[] { job } };
            }
            else throw new InvalidOperationException("Unexpected CI fixture request");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
        private const string RunUrl = "https://api.github.com/repos/owner/repo/actions/runs/90";
    }
}
