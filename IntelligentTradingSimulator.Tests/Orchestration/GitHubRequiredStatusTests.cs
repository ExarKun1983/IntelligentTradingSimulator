using System.Net;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class GitHubRequiredStatusTests
{
    private static readonly string Sha = new('a', 40);
    private static readonly string Definition = new('b', 40);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00.500Z");
    private static readonly GitHubStatusPublisher Publisher = new(9, "trusted[bot]", "BOT_9", "Bot");
    private static readonly GitHubRequiredStatusApproval Approved = new(true, true, true, true, true);

    [Theory]
    [InlineData("preflight", "RequiredStatusPreflight", "HttpRequestException", 403, 0)]
    [InlineData("lookup", "RequiredStatusLookup", "HttpRequestException", 403, 0)]
    [InlineData("boundary", "RequiredStatusBoundary", "IOException", null, 0)]
    [InlineData("post", "RequiredStatusPublication", "HttpRequestException", null, 1)]
    [InlineData("confirmation", "RequiredStatusConfirmation", "InvalidDataException", null, 1)]
    public async Task HandledStatusFailureReportsItsStageWithoutRetryingOrExposingContent(
        string failure, string expectedStage, string expectedError, int? httpStatus, int posts)
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var store = new Store(reducer.Reconcile(State(), Live()).State);
        var intentId = Intent(store.Value).Id;
        using var api = new Api
        {
            ForbiddenSuffix = failure == "preflight" ? "/pulls/17" : failure == "lookup" ? "/status" : null,
            LostResponse = failure == "post",
            Corruption = failure == "confirmation" ? "status-id-url" : null
        };
        if (failure == "boundary")
            store.RejectSave = next => Intent(next).RequiredStatusBoundary is not null
                ? throw new IOException("private persistence input") : false;
        using var client = api.Client();
        using var output = new StringWriter();
        var diagnostics = new GitHubLiveDiagnostics(output);
        var adapter = new GitHubRequiredStatusAdapter(new(client, "owner/repo", 1), store, Publisher, Approved,
            new Clock(), diagnostics);
        var reconciler = new OrchestrationReconciler(reducer, diagnostics);

        await reconciler.RunAsync(store, new Observations(), new RecordingEffects(adapter),
            new(false, true, true, true, AllowedEffectKinds: new HashSet<EffectKind> { EffectKind.PublishRequiredStatus }));

        Assert.Equal(DeliveryStatus.Unknown, store.Value.Outbox[intentId].Status);
        Assert.Equal(EffectFailureCode.ExecutionException, store.Value.Outbox[intentId].FailureCode);
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.Equal(posts, api.Posts);
        using var document = JsonDocument.Parse(output.ToString());
        var diagnostic = document.RootElement.GetProperty("EffectDiagnostic");
        Assert.Equal(expectedStage, diagnostic.GetProperty("Stage").GetString());
        Assert.Equal("PublishRequiredStatus", diagnostic.GetProperty("Effect").GetString());
        Assert.Equal("ExecutionException", diagnostic.GetProperty("FailureCode").GetString());
        Assert.Equal(expectedError, diagnostic.GetProperty("ErrorType").GetString());
        Assert.Equal(httpStatus, diagnostic.GetProperty("HttpStatus").Deserialize<int?>());
        Assert.Equal(5, diagnostic.EnumerateObject().Count());
        Assert.DoesNotContain("private", output.ToString());
        Assert.DoesNotContain("owner/repo", output.ToString());
        Assert.DoesNotContain(Sha, output.ToString());

        diagnostics.Mark(GitHubLiveStage.CiRunIdentity);
        using var unhandled = new StringWriter();
        diagnostics.WriteFailure(unhandled, new InvalidDataException("private later input"));
        Assert.Contains("\"Stage\":\"CiRunIdentity\"", unhandled.ToString());
    }

    [Fact]
    public async Task HandledStatusRecoveryFailureIsReportedWithoutAnotherPost()
    {
        var store = new Store(Planned());
        using var api = new Api { LostResponse = true };
        using var client = api.Client();
        using var output = new StringWriter();
        var diagnostics = new GitHubLiveDiagnostics(output);
        var adapter = new GitHubRequiredStatusAdapter(new(client, "owner/repo", 1), store, Publisher, Approved,
            new Clock(), diagnostics);
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        var intentId = Intent(store.Value).Id;
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, intentId, new(DeliveryStatus.Unknown), Now);
        api.ForbiddenSuffix = "/status";

        await new OrchestrationReconciler(diagnostics: diagnostics).RecoverInFlightAsync(store,
            new RecordingEffects(adapter), Now, new(false, true, true, true));

        Assert.Equal(1, api.Posts);
        Assert.Equal(DeliveryStatus.Unknown, store.Value.Outbox[intentId].Status);
        using var document = JsonDocument.Parse(output.ToString());
        var diagnostic = document.RootElement.GetProperty("EffectDiagnostic");
        Assert.Equal("RequiredStatusLookup", diagnostic.GetProperty("Stage").GetString());
        Assert.Equal("RecoveryException", diagnostic.GetProperty("FailureCode").GetString());
        Assert.Equal(403, diagnostic.GetProperty("HttpStatus").GetInt32());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulStatusCallsRestoreCallerStageEvenWhenRecoveryFindsNoReceipt(bool missingReceipt)
    {
        var store = new Store(Planned());
        using var api = new Api();
        using var client = api.Client();
        using var output = new StringWriter();
        var diagnostics = new GitHubLiveDiagnostics(output);
        var adapter = new GitHubRequiredStatusAdapter(new(client, "owner/repo", 1), store, Publisher, Approved,
            new Clock(), diagnostics);
        diagnostics.Mark(GitHubLiveStage.EffectExecution);

        await adapter.ExecuteAsync(Intent(store.Value), default);

        Assert.Equal(GitHubLiveStage.EffectExecution, diagnostics.Stage);
        diagnostics.Mark(GitHubLiveStage.EffectRecovery);
        api.HideCreatedStatus = missingReceipt;
        var recovered = await adapter.ReconcileAsync(Intent(store.Value), default);
        Assert.Equal(missingReceipt ? DeliveryStatus.Unknown : DeliveryStatus.Succeeded, recovered.Status);
        Assert.Equal(GitHubLiveStage.EffectRecovery, diagnostics.Stage);
        Assert.Equal(1, api.Posts);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task CommandOnlyRevocationReportsHandledFailureThroughHostDiagnostics()
    {
        var store = new Store(Planned());
        using var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        api.ForbiddenSuffix = "/status";
        using var output = new StringWriter();
        var diagnostics = new GitHubLiveDiagnostics(output);

        var result = await PauseCommandOnlyAsync(client, store, Approved, diagnostics: diagnostics);

        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        Assert.True(store.Value.Issues[7].Paused);
        Assert.Equal(1, api.Posts);
        using var document = JsonDocument.Parse(output.ToString());
        var diagnostic = document.RootElement.GetProperty("EffectDiagnostic");
        Assert.Equal("RequiredStatusLookup", diagnostic.GetProperty("Stage").GetString());
        Assert.Equal("ExecutionException", diagnostic.GetProperty("FailureCode").GetString());
        Assert.DoesNotContain("private", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandOnlyPauseRevokesPublishedSuccessWithoutDownstreamApprovalOrQueuedWork(bool uncertainSuccess)
    {
        var store = new Store(Planned());
        var api = new Api { LostResponse = uncertainSuccess };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        var successId = Intent(store.Value).Id;
        if (uncertainSuccess)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
            store.Value = new OrchestrationReducer().RecordOutcome(store.Value, successId, new(DeliveryStatus.Unknown), Now);
        }
        else
        {
            var success = await adapter.ExecuteAsync(Intent(store.Value), default);
            store.Value = new OrchestrationReducer().RecordOutcome(store.Value, successId, success, Now);
        }
        api.LostResponse = false;
        store.Value.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        var queued = JsonSerializer.Serialize(store.Value.Issues[8], StateJson.Options);
        var admission = store.Value.AdmittedIssue;
        var existingIntents = store.Value.Outbox.Keys.ToHashSet();
        api.Requests.Clear();

        var result = await PauseCommandOnlyAsync(client, store, Approved);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(0, result.Iterations);
        Assert.True(store.Value.Issues[7].Paused);
        Assert.Equal(admission, store.Value.AdmittedIssue);
        Assert.Equal(queued, JsonSerializer.Serialize(store.Value.Issues[8], StateJson.Options));
        Assert.Equal(uncertainSuccess ? DeliveryStatus.Unknown : DeliveryStatus.Succeeded, store.Value.Outbox[successId].Status);
        Assert.Equal("error", Intent(store.Value).RequiredStatus!.State);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.Equal(Sha, Intent(store.Value).Sha);
        Assert.Equal(RequiredStatusPolicy.Context, Intent(store.Value).RequiredStatus!.Context);
        Assert.Equal(Intent(store.Value).Id,
            Assert.Single(store.Value.Outbox.Values, intent => !existingIntents.Contains(intent.Id)).Id);
        Assert.Equal(2, api.Posts);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain(api.Requests, path => path.Contains("/agents/", StringComparison.Ordinal) ||
            path.Contains("/actions/runs/90", StringComparison.Ordinal) ||
            path.Contains("/requested_reviewers", StringComparison.Ordinal) ||
            path.Contains("/issues/8", StringComparison.Ordinal) ||
            path.Contains("/comments", StringComparison.Ordinal) || path.Contains("/labels", StringComparison.Ordinal) ||
            path.Contains("/dispatches", StringComparison.Ordinal));

        await PauseCommandOnlyAsync(client, store, Approved);
        Assert.Equal(2, api.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingPauseDoesNotInvalidateExecutingRevocation(bool afterBoundary)
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        Task<WorkerResult>? concurrent = null;
        api.OnPullRead = () =>
        {
            if (concurrent is not null || (Intent(store.Value).RequiredStatusBoundary is not null) != afterBoundary)
                return Task.CompletedTask;
            concurrent = PauseCommandOnlyAsync(client, store, Approved);
            Assert.Equal(DeliveryStatus.Executing, Intent(store.Value).Status);
            Assert.Equal(1, api.Posts);
            return Task.CompletedTask;
        };

        var result = await PauseCommandOnlyAsync(client, store, Approved);

        Assert.NotNull(concurrent);
        Assert.Equal(WorkerStopReason.EffectsDisabled, (await concurrent).Reason);
        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.Equal("error", Intent(store.Value).RequiredStatus!.State);
        Assert.Equal(2, api.Posts);
        await PauseCommandOnlyAsync(client, store, Approved);
        Assert.Equal(2, api.Posts);
    }

    [Fact]
    public async Task PausePersistsBeforeInFlightWorkerSuccessAndRevokesItAfterWorkerReleasesLease()
    {
        var state = Planned();
        Intent(state).Status = DeliveryStatus.Pending;
        var store = new Store(state);
        var api = new Api();
        using var client = api.Client();
        var atSuccessPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccessPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforePostAsync = async () =>
        {
            if (Intent(store.Value).RequiredStatus?.State != "success") return;
            atSuccessPost.TrySetResult();
            await releaseSuccessPost.Task;
        };
        var worker = new OrchestrationWorker(new OrchestrationReducer(new Allow(), requiredStatusEnabled: true), new Clock());
        var running = worker.RunAsync(store, new EmptyEvents(), new Observations(),
            new RecordingEffects(Adapter(client, store)),
            new ReconcileOptions(false, true, true, true, AllowOfflineAgentSimulation: true),
            new WorkerOptions { MaximumIterations = 1, PollInterval = TimeSpan.Zero });
        await atSuccessPost.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Task<WorkerResult>? pause = null;
        try
        {
            pause = PauseCommandOnlyAsync(client, store, Approved);
            Assert.True(store.Value.Issues[7].Paused);
            Assert.False(pause.IsCompleted);
            Assert.Equal(0, api.Posts);
        }
        finally
        {
            releaseSuccessPost.TrySetResult();
            await running;
        }

        Assert.Equal(WorkerStopReason.EffectsDisabled, (await pause!).Reason);
        Assert.Equal(2, api.Posts);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.True(store.Value.Issues[7].Paused);
    }

    [Fact]
    public async Task ActivePausePublisherPreventsWorkerFromRecoveringUnpreparedRevocation()
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnPullRead = async () =>
        {
            if (Intent(store.Value).RequiredStatus?.State != "error" ||
                Intent(store.Value).RequiredStatusBoundary is not null) return;
            publishing.TrySetResult();
            await release.Task;
        };
        var pause = PauseCommandOnlyAsync(client, store, Approved);
        await publishing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.True(store.Value.Issues[7].Paused);
            Assert.Equal(DeliveryStatus.Executing, Intent(store.Value).Status);
            Assert.Null(Intent(store.Value).RequiredStatusBoundary);
            var worker = new OrchestrationWorker(new OrchestrationReducer(new Allow(), requiredStatusEnabled: true), new Clock());
            var result = await worker.RunAsync(store, new EmptyEvents(), new Observations(),
                new RecordingEffects(Adapter(client, store)),
                new ReconcileOptions(false, true, true, true, AllowOfflineAgentSimulation: true),
                new WorkerOptions { MaximumIterations = 1, PollInterval = TimeSpan.Zero });
            Assert.Equal(WorkerStopReason.LeaseHeld, result.Reason);
            Assert.Equal(DeliveryStatus.Executing, Intent(store.Value).Status);
            Assert.Equal(1, api.Posts);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(WorkerStopReason.EffectsDisabled, (await pause).Reason);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.Equal(2, api.Posts);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancellingPauseWhileWaitingForLeaseKeepsTheCommandWithoutPublishing()
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new OrchestrationWorker(timeProvider: new Clock()).RunExclusiveAsync(store, async _ =>
        {
            acquired.TrySetResult();
            await release.Task;
            return true;
        });
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new GitHubLiveDiagnostics();
        try
        {
            var pause = PauseCommandOnlyAsync(client, store, Approved, token: cancellation.Token,
                diagnostics: diagnostics);
            Assert.True(store.Value.Issues[7].Paused);
            Assert.False(pause.IsCompleted);
            cancellation.Cancel();
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause);
            using var output = new StringWriter();
            diagnostics.WriteFailure(output, exception);
            Assert.Contains("\"Stage\":\"WorkerLease\"", output.ToString());
            Assert.True(store.Value.Issues[7].Paused);
            Assert.Equal(0, api.Posts);
        }
        finally
        {
            release.TrySetResult();
            await owner;
        }
    }

    [Theory]
    [InlineData("lost-response")]
    [InlineData("publisher")]
    [InlineData("head")]
    public async Task CommandOnlyRevocationPreservesPauseAndNeverBlindlyRepeatsPost(string failure)
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        store.Value.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        api.LostResponse = failure == "lost-response";
        api.HideCreatedStatus = failure == "lost-response";
        api.Corruption = failure == "publisher" ? "actor" : failure == "head" ? "head" : null;

        var result = await PauseCommandOnlyAsync(client, store, Approved);

        Assert.Equal(failure == "head" ? WorkerStopReason.EffectsDisabled : WorkerStopReason.Blocked, result.Reason);
        Assert.True(store.Value.Issues[7].Paused);
        Assert.Empty(store.Value.Issues[8].Tasks);
        Assert.Equal(failure == "head" ? DeliveryStatus.Succeeded : DeliveryStatus.Unknown, Intent(store.Value).Status);
        Assert.Equal("error", Intent(store.Value).RequiredStatus!.State);
        var posts = api.Posts;
        if (failure == "head")
        {
            Assert.Equal(2, posts);
            Assert.Equal(Sha, Intent(store.Value).Sha);
            Assert.NotNull(Intent(store.Value).RequiredStatusBoundary);
        }
        await PauseCommandOnlyAsync(client, store, Approved);
        Assert.Equal(posts, api.Posts);
        if (failure == "lost-response")
        {
            api.HideCreatedStatus = false;
            api.LostResponse = false;
            var recovered = await PauseCommandOnlyAsync(client, store, Approved);
            Assert.Equal(WorkerStopReason.EffectsDisabled, recovered.Reason);
            Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
            Assert.True(store.Value.Issues[7].Paused);
            Assert.Equal(posts, api.Posts);
        }
    }

    [Fact]
    public async Task CommandOnlyRevocationCancellationLeavesExecutingIntentForLookupOnlyReplay()
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        api.CancelAfterPost = true;

        await Assert.ThrowsAsync<OperationCanceledException>(() => PauseCommandOnlyAsync(client, store, Approved));

        Assert.True(store.Value.Issues[7].Paused);
        Assert.Equal(DeliveryStatus.Executing, Intent(store.Value).Status);
        Assert.NotNull(Intent(store.Value).RequiredStatusBoundary);
        Assert.Equal("error", Intent(store.Value).RequiredStatus!.State);
        api.CancelAfterPost = false;
        var recovered = await PauseCommandOnlyAsync(client, store, Approved);
        Assert.Equal(WorkerStopReason.EffectsDisabled, recovered.Reason);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.Equal(2, api.Posts);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Executing)]
    [InlineData(DeliveryStatus.Succeeded)]
    public async Task StatusOnlyRevocationCasConflictsNeverRepeatTheCreate(DeliveryStatus conflictAt)
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        var conflict = false;
        store.RejectSave = next =>
        {
            if (conflict || Intent(next) is not { RequiredStatus.State: "error" } intent ||
                intent.Status != conflictAt) return false;
            conflict = true;
            return true;
        };

        await PauseCommandOnlyAsync(client, store, Approved);

        Assert.True(conflict);
        Assert.True(store.Value.Issues[7].Paused);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
        Assert.Equal(2, api.Posts);
    }

    [Fact]
    public void StatusOnlyPlannerCannotPublishSuccessOrAdmitQueuedWork()
    {
        var state = State();
        state.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        var reducer = new OrchestrationReducer();
        Assert.Throws<InvalidOperationException>(() => reducer.PlanRequiredStatusRevocation(state, 7, Now));
        state.Issues[7].Paused = true;
        var planned = reducer.PlanRequiredStatusRevocation(state, 7, Now);
        Assert.Equal("error", Intent(planned).RequiredStatus!.State);
        Assert.Equal(state.AdmittedIssue, planned.AdmittedIssue);
        Assert.Empty(planned.Issues[8].Tasks);
        Assert.Equal(StateJson.Serialize(planned),
            StateJson.Serialize(reducer.PlanRequiredStatusRevocation(planned, 7, Now)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandOnlyPauseWithoutStatusAuthorizationSavesPauseButExplicitlyFailsRevocation(bool missingPublisher)
    {
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var success = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, success, Now);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PauseCommandOnlyAsync(client, store, missingPublisher ? Approved : new(), missingPublisher));

        Assert.Contains("revocation prerequisites", error.Message);
        Assert.True(store.Value.Issues[7].Paused);
        Assert.Equal(1, api.Posts);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(store.Value).Status);
    }

    private static Task<WorkerResult> PauseCommandOnlyAsync(HttpClient client, Store store,
        GitHubRequiredStatusApproval approval, bool missingPublisher = false, CancellationToken token = default,
        GitHubLiveDiagnostics? diagnostics = null)
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            repository = new { id = 1, full_name = "owner/repo" },
            sender = new { id = 55, login = "maintainer", type = "User" },
            inputs = new { issue = "7", operation = "pause", reason = "Operator pause after published success." }
        });
        return new GitHubOrchestrationHost(new(client, "owner/repo", 1), store, new Clock(), liveDiagnostics: diagnostics)
            .ReconcileAsync(payload, "workflow_dispatch", "44", 1, 1, Now, [], 9, "master", 55,
                new(true, true, true, true), token: token, reviewApproval: new(), ciApproval: new(),
                statusPublisher: missingPublisher ? null : Publisher, statusApproval: approval, commandOnly: true);
    }

    [Fact]
    public async Task ExactTestedShaNotDefinitionShaIsPublishedAfterDurableBoundary()
    {
        var store = new Store(Planned());
        var api = new Api();
        api.BeforePost = () => Assert.NotNull(Intent(store.Value).RequiredStatusBoundary);
        using var client = api.Client();
        var adapter = Adapter(client, store);
        var result = await adapter.ExecuteAsync(Intent(store.Value), default);
        Assert.Equal(new(DeliveryStatus.Succeeded, "100"), result);
        Assert.Equal(1, api.Posts);
        Assert.Contains($"/statuses/{Sha}", api.Requests);
        Assert.DoesNotContain($"/statuses/{Definition}", api.Requests);
        Assert.Equal("success", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.Equal(100, Intent(store.Value).RequiredStatusBoundary!.StatusId);
        Assert.Equal(result, await adapter.ReconcileAsync(Intent(store.Value), default));
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task LostResponseAndOutcomeRecoveryAreLookupOnlyAndDoNotClearHold()
    {
        var store = new Store(Planned());
        var api = new Api { LostResponse = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        var id = Intent(store.Value).Id;
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, id, new(DeliveryStatus.Unknown), Now);
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Equal(new(DeliveryStatus.Succeeded, "100"), await adapter.ReconcileAsync(Intent(store.Value), default));
        Assert.Equal(new(DeliveryStatus.Succeeded, "100"), await adapter.ReconcileAsync(Intent(store.Value), default));
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Equal(1, api.Posts);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task RecoveredLostSuccessThenFeedbackPublishesFreshErrorBeforeAnyNewWork()
    {
        var store = new Store(Planned());
        var api = new Api { LostResponse = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        var successId = Intent(store.Value).Id;
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        store.Value = reducer.RecordOutcome(store.Value, successId, new(DeliveryStatus.Unknown), Now);
        api.LostResponse = false;
        var effects = new RecordingEffects(adapter);
        var worker = new OrchestrationWorker(reducer, new Clock());
        var result = await worker.RunAsync(store, new FeedbackEvents(), new Observations(), effects,
            new(false, true, true, true), new() { MaximumIterations = 5, PollInterval = TimeSpan.Zero });
        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        Assert.Equal(DeliveryStatus.Succeeded, store.Value.Outbox[successId].Status);
        Assert.Equal("100", store.Value.Outbox[successId].ExternalId);
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Contains("feedback-after-loss", store.Value.Issues[7].PendingFeedbackEventIds);
        Assert.Equal(EffectKind.PublishRequiredStatus, effects.Executed[0].Kind);
        Assert.Equal("error", effects.Executed[0].RequiredStatus!.State);
        Assert.DoesNotContain(effects.Executed, effect => effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
        Assert.DoesNotContain(effects.Executed, effect => effect.RequiredStatus?.State == "success");
        Assert.Equal(2, api.Posts);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.Equal("101", Intent(store.Value).ExternalId);
    }

    [Fact]
    public async Task UnknownSuccessCanOnlyBeCorrectedBySeparateDurableErrorAndNeverRetried()
    {
        var store = new Store(Planned());
        var api = new Api { LostResponse = true, HideFirstStatus = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        var successId = Intent(store.Value).Id;
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        store.Value = reducer.RecordOutcome(store.Value, successId, new(DeliveryStatus.Unknown), Now);
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(store.Value.Outbox[successId], default)).Status);
        store.Value = reducer.Reconcile(store.Value, Live()).State;
        var errorId = Intent(store.Value).Id;
        Assert.NotEqual(successId, errorId);
        Assert.Equal("error", Intent(store.Value).RequiredStatus!.State);
        store.Value = reducer.MarkExecuting(store.Value, errorId, Now);
        api.LostResponse = false;
        var correction = await adapter.ExecuteAsync(store.Value.Outbox[errorId], default);
        store.Value = reducer.RecordOutcome(store.Value, errorId, correction, Now);
        Assert.Equal(DeliveryStatus.Unknown, store.Value.Outbox[successId].Status);
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.Equal(2, api.Posts);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        api.HideFirstStatus = false;
        var receipt = await adapter.ReconcileAsync(store.Value.Outbox[successId], default);
        Assert.Equal(new(DeliveryStatus.Succeeded, "100"), receipt);
        store.Value = reducer.RecordOutcome(store.Value, successId, receipt, Now);
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Equal(2, api.Posts);
    }

    [Theory]
    [InlineData("before-boundary")]
    [InlineData("after-boundary")]
    [InlineData("after-post")]
    public async Task CrashAtPersistenceBoundaryNeverBlindlyCreatesAgain(string crash)
    {
        var store = new Store(Planned());
        var api = new Api();
        if (crash == "before-boundary") store.FailSave = true;
        if (crash == "after-boundary") api.AfterPullRead = () =>
        {
            if (Intent(store.Value).RequiredStatusBoundary is not null) throw new IOException("offline crash");
        };
        if (crash == "after-post") api.BeforePost = () => store.FailSave = true;
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAnyAsync<Exception>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        store.FailSave = false;
        api.AfterPullRead = null;
        var reconciled = await adapter.ReconcileAsync(Intent(store.Value), default);
        Assert.Equal(crash == "after-post" ? DeliveryStatus.Succeeded : DeliveryStatus.Unknown, reconciled.Status);
        Assert.Equal(crash == "after-post" ? 1 : 0, api.Posts);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("node")]
    [InlineData("actor-type")]
    [InlineData("repository")]
    [InlineData("status-sha")]
    [InlineData("status-id-url")]
    [InlineData("context")]
    [InlineData("target")]
    [InlineData("description")]
    [InlineData("state")]
    [InlineData("created")]
    [InlineData("future")]
    [InlineData("lookup-sha")]
    [InlineData("duplicate")]
    public async Task UncertainCreateCannotAdoptUntrustedOrAmbiguousStatus(string corruption)
    {
        var store = new Store(Planned());
        var api = new Api { LostResponse = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<HttpRequestException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        api.Corruption = corruption;
        if (corruption is "duplicate" or "context" or "target")
            Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(Intent(store.Value), default)).Status);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ReconcileAsync(Intent(store.Value), default));
        Assert.Equal(1, api.Posts);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("issue-closed")]
    [InlineData("pull-closed")]
    [InlineData("run-sha")]
    [InlineData("run-repository")]
    [InlineData("run-path")]
    [InlineData("run-attempt")]
    [InlineData("run-conclusion")]
    [InlineData("definition")]
    [InlineData("job-name")]
    [InlineData("job-sha")]
    [InlineData("job-count")]
    [InlineData("step")]
    public async Task LiveHeadAndTrustedExactShaEvidenceFailClosedBeforePost(string corruption)
    {
        var store = new Store(Planned());
        var api = new Api { Corruption = corruption };
        using var client = api.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => Adapter(client, store).ExecuteAsync(Intent(store.Value), default));
        Assert.Equal(0, api.Posts);
    }

    [Theory]
    [InlineData("feedback")]
    [InlineData("paused")]
    [InlineData("head")]
    [InlineData("task")]
    [InlineData("round")]
    [InlineData("stopped")]
    public async Task GuardChangedAfterBoundaryCannotPublishSuccess(string change)
    {
        var store = new Store(Planned());
        var api = new Api();
        api.AfterPullRead = () =>
        {
            if (Intent(store.Value).RequiredStatusBoundary is null) return;
            var issue = store.Value.Issues[7];
            switch (change)
            {
                case "feedback": issue.PendingFeedbackEventIds.Add("human"); break;
                case "paused": issue.Paused = true; break;
                case "head": issue.HeadSha = Definition; break;
                case "task": issue.Tasks["task"].Status = TaskStatus.Running; break;
                case "round": issue.Round++; break;
                case "stopped": issue.Ci!.TerminalStopReason = "cancelled"; break;
            }
        };
        using var client = api.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => Adapter(client, store).ExecuteAsync(Intent(store.Value), default));
        Assert.Equal(0, api.Posts);
    }

    [Fact]
    public async Task AllApprovalsDefaultFalseAndEachMissingApprovalPreventsIo()
    {
        foreach (var approval in new[] { new GitHubRequiredStatusApproval(), Approved with { ActivationApproved = false },
                     Approved with { CredentialActorVerified = false }, Approved with { TrustedPublisherVerified = false },
                     Approved with { RequiredContextVerified = false }, Approved with { NoAdditionalChargesVerified = false } })
        {
            var store = new Store(Planned());
            var api = new Api();
            using var client = api.Client();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new GitHubRequiredStatusAdapter(new(client, "owner/repo", 1), store, Publisher, approval, new Clock())
                    .ExecuteAsync(Intent(store.Value), default));
            Assert.Empty(api.Requests);
        }
    }

    [Fact]
    public async Task ManualMergeGateStillRequiresTrustedPublisherAndPublishesOnlyCanonicalStatus()
    {
        var approval = Approved with { RequiredContextVerified = false, ManualMergeGateApproved = true };
        var store = new Store(Planned());
        var api = new Api();
        using var client = api.Client();
        var adapter = new GitHubRequiredStatusAdapter(new(client, "owner/repo", 1), store, Publisher, approval, new Clock());

        var result = await adapter.ExecuteAsync(Intent(store.Value), default);

        Assert.Equal(DeliveryStatus.Succeeded, result.Status);
        Assert.Equal(1, api.Posts);
        Assert.Equal("success", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.False((approval with { TrustedPublisherVerified = false }).AllowsPublication);
        Assert.False((approval with { CredentialActorVerified = false }).AllowsPublication);
        Assert.False((approval with { ActivationApproved = false }).AllowsPublication);
        Assert.False((approval with { NoAdditionalChargesVerified = false }).AllowsPublication);
    }

    [Fact]
    public async Task ConcurrentPublishersCannotCreateTwice()
    {
        var store = new Store(Planned());
        var api = new Api { SynchronizePreparation = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        async Task<bool> Execute()
        {
            try { await adapter.ExecuteAsync(Intent(store.Value), default); return true; }
            catch (InvalidDataException) { return false; }
        }
        var outcomes = await Task.WhenAll(Execute(), Execute());
        Assert.Single(outcomes, result => result);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task CancellationAfterCreationRemainsLookupOnly()
    {
        var store = new Store(Planned());
        var api = new Api { CancelAfterPost = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<OperationCanceledException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        api.CancelAfterPost = false;
        Assert.Equal(new(DeliveryStatus.Succeeded, "100"), await adapter.ReconcileAsync(Intent(store.Value), default));
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task StoppedGenerationCanPublishErrorButNeverSuccess()
    {
        var state = State();
        state.Issues[7].Phase = Phase.BLOCKED;
        state.Issues[7].ReactivationRequired = true;
        state.Issues[7].ActiveAuthorized = false;
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        state = reducer.Reconcile(state, Live()).State;
        var store = new Store(reducer.MarkExecuting(state, Intent(state).Id, Now));
        var api = new Api();
        using var client = api.Client();
        Assert.Equal(DeliveryStatus.Succeeded, (await Adapter(client, store).ExecuteAsync(Intent(store.Value), default)).Status);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Null(store.Value.AdmittedIssue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedMergePublishesSuccessAndReleasesTerminalProjectionBarrier(bool issueClosed)
    {
        var state = MergeReady();
        state.Issues[7].ContinuationHoldBarrier = new(1, true);
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestClosed = true;
        live.Issues[7].PullRequestMergedAt = Now;
        live.Issues[7].Labels = [];
        var store = new Store(state);
        using var api = new Api { Merged = true, Corruption = issueClosed ? "issue-closed" : null };
        using var client = api.Client();
        var result = await new OrchestrationWorker(new OrchestrationReducer(requiredStatusEnabled: true), new Clock())
            .RunAsync(store, new EmptyEvents(), new Observations(live), new RecordingEffects(Adapter(client, store)),
                new(false, true, true, true),
                new WorkerOptions { MaximumIterations = 8, PollInterval = TimeSpan.Zero });

        Assert.Equal(WorkerStopReason.Settled, result.Reason);
        Assert.Equal(1, api.Posts);
        Assert.Equal("success", Intent(store.Value).RequiredStatus!.State);
        Assert.Equal("Merged; exact tested head CI and accepted review verified.", Intent(store.Value).RequiredStatus!.Description);
        Assert.Equal(Phase.CLOSED, store.Value.Issues[7].Phase);
        Assert.Equal(TerminalOutcome.Merged, store.Value.Issues[7].TerminalOutcome);
        Assert.Null(store.Value.Issues[7].ContinuationHoldBarrier);
        Assert.Null(store.Value.AdmittedIssue);
        Assert.DoesNotContain(store.Value.Outbox.Values,
            effect => effect.Kind is EffectKind.LaunchTask or EffectKind.RequestReview);
    }

    [Theory]
    [InlineData(DeliveryStatus.Failed)]
    [InlineData(DeliveryStatus.Unknown)]
    public void LinkRevocationObligationSurvivesPersistenceAndReceiptRecoveryUntilMatchingErrorIsConfirmed(
        DeliveryStatus outcome)
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        var linkId = Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue).Id;
        var statusId = Intent(state).Id;
        state = reducer.MarkExecuting(state, statusId, Now);
        state = reducer.RecordOutcome(state, statusId, new(DeliveryStatus.Succeeded, "status-receipt"), Now);
        state = reducer.MarkExecuting(state, linkId, Now);
        state = reducer.RecordOutcome(state, linkId, new(outcome), Now);
        state = StateJson.Deserialize(StateJson.Serialize(state));
        Assert.True(state.Outbox[linkId].IssueLinkRevocationRequired);
        if (outcome == DeliveryStatus.Unknown)
            state = reducer.RecordOutcome(state, linkId, new(DeliveryStatus.Succeeded, "PR_node"), Now);
        Assert.True(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));

        state = reducer.PlanRequiredStatusRevocation(state, 7, Now);
        var errorId = Intent(state).Id;
        var unrelated = StateJson.Clone(state).Outbox[errorId];
        unrelated.Id = "unrelated-error";
        unrelated.Digest = "unrelated-error";
        state.Outbox.Add(unrelated.Id, unrelated);
        state = reducer.MarkExecuting(state, unrelated.Id, Now);
        state = reducer.RecordOutcome(state, unrelated.Id, new(DeliveryStatus.Succeeded, "other-receipt"), Now);
        Assert.True(state.Outbox[linkId].IssueLinkRevocationRequired);

        state = reducer.MarkExecuting(state, errorId, Now);
        state = reducer.RecordOutcome(state, errorId, new(DeliveryStatus.Unknown), Now);
        Assert.True(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
        state = reducer.RecordOutcome(state, errorId, new(DeliveryStatus.Succeeded, "error-receipt"), Now);
        state = StateJson.Deserialize(StateJson.Serialize(state));

        Assert.False(state.Outbox[linkId].IssueLinkRevocationRequired);
        Assert.False(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
    }

    [Fact]
    public void StateValidationRejectsUnversionedIssueLinkIntent()
    {
        var state = State();
        state.Outbox["unversioned-link"] = new()
        {
            Id = "unversioned-link", IssueNumber = 7, Generation = 1, Kind = EffectKind.LinkIssue,
            Status = DeliveryStatus.Unknown
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            new OrchestrationReducer().Reconcile(state, Live()));

        Assert.Contains("IssueLink payload with protocol version 1", error.Message);
    }

    [Fact]
    public void StatuslessLinkNotStartedFailureCanBeReactivated()
    {
        var reducer = new OrchestrationReducer(new Allow());
        var state = reducer.Reconcile(State(), Live()).State;
        var link = Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue);
        Assert.DoesNotContain(state.Outbox.Values, effect => effect.IssueNumber == 7 &&
            effect.Kind == EffectKind.PublishRequiredStatus);
        state = reducer.MarkExecuting(state, link.Id, Now);
        state = reducer.RecordOutcome(state, link.Id, new(DeliveryStatus.Failed,
            FailureCode: EffectFailureCode.IssueLinkNotStarted, IssueLinkFailure: new(null)), Now);

        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.False(state.Outbox[link.Id].IssueLinkRevocationRequired);
        state = reducer.Apply(state, new("reactivate", "owner/repo", 7, 1, 1,
            new("operator", ActorKind.Human), Now, new Reactivate("Retry after confirming no status was published.")));

        Assert.Equal(2, state.Issues[7].Generation);
    }

    [Fact]
    public async Task ConfirmedLinkRevocationRemainsMatchableInCommandOnlyRecovery()
    {
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        var link = Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue);
        var success = Intent(state);
        state = reducer.MarkExecuting(state, success.Id, Now);
        state = reducer.RecordOutcome(state, success.Id, new(DeliveryStatus.Succeeded, "success-receipt"), Now);
        state = reducer.MarkExecuting(state, link.Id, Now);
        state = reducer.RecordOutcome(state, link.Id, new(DeliveryStatus.Failed), Now);
        var store = new Store(state);
        using var api = new Api();
        using var client = api.Client();

        var first = await PauseCommandOnlyAsync(client, store, Approved);
        var revocation = Intent(store.Value);

        Assert.Equal(WorkerStopReason.EffectsDisabled, first.Reason);
        Assert.Equal(DeliveryStatus.Succeeded, revocation.Status);
        Assert.False(store.Value.Outbox[link.Id].IssueLinkRevocationRequired);
        Assert.Equal(revocation.Id, store.Value.Outbox[link.Id].IssueLinkRevocationDischargedBy);
        Assert.True(RequiredStatusPolicy.Matches(store.Value, store.Value.Issues[7], revocation));
        Assert.Equal(StateJson.Serialize(store.Value),
            StateJson.Serialize(reducer.PlanRequiredStatusRevocation(store.Value, 7, Now)));

        var recovered = await PauseCommandOnlyAsync(client, store, Approved);

        Assert.Equal(WorkerStopReason.EffectsDisabled, recovered.Reason);
        Assert.Equal(1, api.Posts);
        Assert.Equal(revocation.Id, Intent(store.Value).Id);
    }

    [Fact]
    public void LinkRevocationUsesRecordedGenerationAndShaAndBlocksReactivationUntilConfirmed()
    {
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        var link = Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue);
        var success = Intent(state);
        state = reducer.MarkExecuting(state, success.Id, Now);
        state = reducer.RecordOutcome(state, success.Id, new(DeliveryStatus.Succeeded, "success-receipt"), Now);
        state = reducer.MarkExecuting(state, link.Id, Now);
        state = reducer.RecordOutcome(state, link.Id, new(DeliveryStatus.Failed), Now);

        var reactivate = new OrchestrationEvent("reactivate", "owner/repo", 7, 1, 1,
            new("operator", ActorKind.Human), Now, new Reactivate("Retry after inspecting the failed link."));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, reactivate));

        state.Issues[7].Generation = 2;
        state.Issues[7].HeadSha = new('c', 40);
        Assert.True(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
        state = reducer.PlanRequiredStatusRevocation(state, 7, Now);
        var revocation = Intent(state);
        Assert.Equal(link.Generation, revocation.Generation);
        Assert.Equal(link.Sha, revocation.Sha);
        Assert.Equal("error", revocation.RequiredStatus!.State);
        Assert.True(RequiredStatusPolicy.Matches(state, state.Issues[7], revocation));

        state = reducer.MarkExecuting(state, revocation.Id, Now);
        state = reducer.RecordOutcome(state, revocation.Id, new(DeliveryStatus.Unknown), Now);
        Assert.True(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
        Assert.Throws<InvalidOperationException>(() => reducer.Apply(state, reactivate with
        {
            Id = "reactivate-after-unknown-status",
            IssueGeneration = 2
        }));
        state = reducer.PlanRequiredStatusRevocation(state, 7, Now);
        Assert.Equal(revocation.Id, Intent(state).Id);
        state = reducer.RecordOutcome(state, revocation.Id, new(DeliveryStatus.Succeeded, "error-receipt"), Now);

        Assert.False(state.Outbox[link.Id].IssueLinkRevocationRequired);
        Assert.False(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
        Assert.Equal(2, state.Issues[7].Generation);
    }

    [Fact]
    public async Task LinkRevocationPublishesToOriginalShaAfterHeadAndGenerationChange()
    {
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        var link = Assert.Single(state.Outbox.Values, effect => effect.Kind == EffectKind.LinkIssue);
        var success = Intent(state);
        state = reducer.MarkExecuting(state, success.Id, Now);
        state = reducer.RecordOutcome(state, success.Id, new(DeliveryStatus.Succeeded, "success-receipt"), Now);
        state = reducer.MarkExecuting(state, link.Id, Now);
        state = reducer.RecordOutcome(state, link.Id, new(DeliveryStatus.Failed), Now);
        state.Issues[7].Generation = 2;
        state.Issues[7].HeadSha = new('c', 40);
        var laterHead = Live();
        laterHead.Issues[7].HeadSha = state.Issues[7].HeadSha;
        state = reducer.Reconcile(state, laterHead).State;
        var currentError = Intent(state);
        Assert.Equal(2, currentError.Generation);
        Assert.Equal(state.Issues[7].HeadSha, currentError.Sha);
        Assert.Equal("error", currentError.RequiredStatus!.State);
        state = reducer.MarkExecuting(state, currentError.Id, Now);
        state = reducer.RecordOutcome(state, currentError.Id, new(DeliveryStatus.Succeeded, "later-error-receipt"), Now);
        Assert.True(state.Outbox[link.Id].IssueLinkRevocationRequired);

        state = reducer.PlanRequiredStatusRevocation(state, 7, Now);
        var revocation = Intent(state);
        Assert.NotEqual(currentError.Id, revocation.Id);
        state = reducer.MarkExecuting(state, revocation.Id, Now);

        var store = new Store(state);
        using var api = new Api();
        using var client = api.Client();
        var outcome = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        state = reducer.RecordOutcome(store.Value, revocation.Id, outcome, Now);

        Assert.Equal(DeliveryStatus.Succeeded, outcome.Status);
        Assert.Equal(1, api.Posts);
        Assert.Equal(Sha, state.Outbox[revocation.Id].Sha);
        Assert.False(state.Outbox[link.Id].IssueLinkRevocationRequired);
        Assert.False(RequiredStatusPolicy.RequiresIssueLinkRevocation(state, state.Issues[7]));
    }

    [Theory]
    [InlineData("unmerged")]
    [InlineData("unknown")]
    [InlineData("missing-record")]
    [InlineData("record-sha")]
    [InlineData("record-pr")]
    [InlineData("record-generation")]
    [InlineData("ci-failed")]
    [InlineData("review-missing")]
    [InlineData("review-failed")]
    [InlineData("unresolved-finding")]
    [InlineData("paused")]
    [InlineData("feedback")]
    [InlineData("uncertain-effect")]
    [InlineData("feedback-proof")]
    [InlineData("approval-proof")]
    [InlineData("handoff")]
    [InlineData("shared-sha")]
    [InlineData("shared-pr")]
    public void ClosedWorkCannotClaimVerifiedMergeWithMissingOrConflictingEvidence(string corruption)
    {
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        var state = new OrchestrationReducer().Reconcile(MergeReady(), live).State;
        var issue = state.Issues[7];
        switch (corruption)
        {
            case "unmerged": issue.TerminalOutcome = TerminalOutcome.Unmerged; break;
            case "unknown": issue.TerminalOutcome = TerminalOutcome.Unknown; break;
            case "missing-record": issue.TerminalHistory.Clear(); break;
            case "record-sha": issue.TerminalHistory[0] = issue.TerminalHistory[0] with { Sha = Definition }; break;
            case "record-pr": issue.TerminalHistory[0] = issue.TerminalHistory[0] with { PullRequest = 18 }; break;
            case "record-generation": issue.TerminalHistory[0] = issue.TerminalHistory[0] with { Generation = 2 }; break;
            case "ci-failed": issue.Ci!.Green = false; break;
            case "review-missing": issue.Review = null; break;
            case "review-failed": issue.Review!.LifecycleState = ReviewLifecycleState.Failed; break;
            case "unresolved-finding": issue.Review!.Findings.Add(new("open", "Needs fix", "evidence")); break;
            case "paused": issue.Paused = true; break;
            case "feedback": issue.PendingFeedbackEventIds.Add("new-feedback"); break;
            case "feedback-proof": issue.MergeFeedback = null; break;
            case "approval-proof":
                issue.TerminalHistory[0] = issue.TerminalHistory[0] with { Approval = null }; break;
            case "handoff": issue.CurrentHandoffId = null; break;
            case "shared-sha": state.Issues[8] = new() { Number = 8, HeadSha = Sha, Phase = Phase.BLOCKED }; break;
            case "shared-pr": state.Issues[8] = new() { Number = 8, PullRequest = 17, Phase = Phase.BLOCKED }; break;
            case "uncertain-effect":
                state.Outbox["unknown"] = new() { Id = "unknown", IssueNumber = 7, Generation = 1,
                    Kind = EffectKind.LinkIssue, Status = DeliveryStatus.Unknown, Sha = Sha,
                    IssueLink = new(1, 17) };
                break;
        }

        Assert.Equal("error", RequiredStatusPolicy.Desired(state, issue).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergeCannotRestoreEligibilityAfterHeadDriftAndRestoration(bool mergeFirst)
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = MergeReady();
        var live = Live();
        if (mergeFirst)
        {
            live.Issues[7].Closed = true;
            live.Issues[7].PullRequestMerged = true;
            live.Issues[7].PullRequestMergedAt = Now;
            state = reducer.Reconcile(state, live).State;
            Assert.Equal("success", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
        }
        live.Issues[7].HeadSha = Definition;
        state = reducer.Reconcile(state, live).State;
        live.Issues[7].HeadSha = Sha;
        state = reducer.Reconcile(state, live).State;
        var hold = state.Issues[7].HoldReason;
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = reducer.Reconcile(state, live).State;

        Assert.Null(state.Issues[7].TerminalHistory.Last().Approval);
        Assert.Equal(hold, state.Issues[7].HoldReason);
        Assert.Equal("error", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Theory]
    [InlineData("safety-hold")]
    [InlineData("missing-handoff")]
    [InlineData("pause")]
    [InlineData("feedback")]
    public void ClosureDoesNotFabricateFinalApprovalOrEraseSafetyHold(string change)
    {
        var state = MergeReady();
        var issue = state.Issues[7];
        switch (change)
        {
            case "safety-hold": issue.Phase = Phase.BLOCKED; issue.HoldReason = "Safety requires reactivation."; break;
            case "missing-handoff": issue.CurrentHandoffId = null; break;
            case "pause": issue.Paused = true; break;
            case "feedback": issue.PendingFeedbackEventIds.Add("feedback"); break;
        }
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(state, live).State;

        Assert.Null(state.Issues[7].TerminalHistory.Last().Approval);
        if (change == "safety-hold") Assert.Equal("Safety requires reactivation.", state.Issues[7].HoldReason);
        Assert.Equal("error", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("issue")]
    [InlineData("pull")]
    [InlineData("edited-after-merge")]
    [InlineData("postmerge-only")]
    public async Task ClosedIssueReadsFeedbackThroughMergeBeforeGrantingTerminalSuccess(string feedback)
    {
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true);
        var state = reducer.ConfirmFinalApprovals(State(), Now.AddSeconds(-1));
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestClosed = false;
        live.Issues[7].PullRequestMerged = false;
        state = reducer.Reconcile(state, live).State;
        Assert.NotNull(state.Issues[7].TerminalHistory.Last().Approval);
        using var api = new Api { Corruption = "issue-closed" };
        using var client = api.Client();
        var source = new GitHubFeedbackSource(new(client, "owner/repo", 1), clock: new Clock(Now.AddMinutes(1)));
        Assert.Empty(await source.ReadAsync(state, default));
        api.Merged = true;
        if (feedback != "none")
        {
            var comment = JsonSerializer.SerializeToElement(new { id = 1, body = "Additional human instruction",
                created_at = feedback == "postmerge-only" ? Now.AddSeconds(1) : Now.AddSeconds(-30),
                updated_at = feedback == "edited-after-merge" ? Now.AddSeconds(1) : Now.AddSeconds(-10),
                user = new { id = 55, login = "maintainer", type = "User" } });
            (feedback == "pull" ? api.PullComments : api.IssueComments).Add(comment);
        }
        var messages = await source.ReadAsync(state, default);
        Assert.Single(messages, message => message.Data is MergeFeedbackObserved);
        var clean = feedback is "none" or "postmerge-only";
        Assert.Equal(clean ? 0 : 1, messages.Count(message => message.Data is HumanFeedback));
        foreach (var message in messages) state = reducer.Apply(state, message);
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestClosed = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = reducer.Reconcile(state, live).State;

        Assert.Equal(clean ? "success" : "error",
            RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
        var commentReads = api.Requests.Count(path => path.EndsWith("/comments", StringComparison.Ordinal));
        Assert.Empty(await source.ReadAsync(state, default));
        Assert.Equal(commentReads, api.Requests.Count(path => path.EndsWith("/comments", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IncompleteFeedbackReadCannotSealMergeBoundary()
    {
        var state = State();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        using var api = new Api { Merged = true, ForbiddenSuffix = "/issues/17/comments" };
        using var client = api.Client();
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new GitHubFeedbackSource(new(client, "owner/repo", 1), clock: new Clock()).ReadAsync(state, default));
        Assert.Null(state.Issues[7].MergeFeedback);
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(state, live).State;
        Assert.Equal("error", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Theory]
    [InlineData(-1, "success")]
    [InlineData(0, "error")]
    [InlineData(1, "error")]
    public async Task DurableHandoffConfirmationMustStrictlyPrecedeMerge(int seconds, string expected)
    {
        var state = State();
        var store = new Store(state);
        await new OrchestrationReconciler().ConfirmFinalApprovalsAsync(store, new Clock(Now.AddSeconds(seconds)), default);
        var issue = store.Value.Issues[7];
        Assert.Equal(Now.AddSeconds(seconds), issue.Handoffs[issue.CurrentHandoffId!].PublicationConfirmedAt);
        issue.MergeFeedback = new(1, 17, Sha, Now, Now.AddMinutes(1));
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(store.Value, live).State;

        Assert.Equal(expected, RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
        if (seconds >= 0) Assert.Null(state.Issues[7].TerminalHistory.Last().Approval);
    }

    [Fact]
    public async Task MergeDuringCommentCollectionExcludesCommentsCreatedAfterMerge()
    {
        var state = MergeReady();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        using var api = new Api();
        api.BeforeComments = () => api.Merged = true;
        api.IssueComments.Add(JsonSerializer.SerializeToElement(new { id = 1, body = "Post-merge note",
            created_at = Now.AddSeconds(1), updated_at = Now.AddSeconds(1),
            user = new { id = 55, login = "maintainer", type = "User" } }));
        using var client = api.Client();
        var messages = await new GitHubFeedbackSource(new(client, "owner/repo", 1),
            clock: new Clock(Now.AddMinutes(1))).ReadAsync(state, default);

        Assert.Single(messages, message => message.Data is MergeFeedbackObserved);
        Assert.DoesNotContain(messages, message => message.Data is HumanFeedback);
        var pullRead = api.Requests.FindLastIndex(path => path == "/pulls/17");
        Assert.True(pullRead > api.Requests.FindLastIndex(path => path.EndsWith("/comments", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedbackPostedBetweenInitialListAndMergeObservationCannotBeSealedAway(bool onPull)
    {
        var state = MergeReady();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        using var api = new Api { Merged = true };
        api.AfterPullRead = () =>
        {
            (onPull ? api.PullComments : api.IssueComments).Add(JsonSerializer.SerializeToElement(new
            {
                id = 1, body = "Instruction posted just before merge",
                created_at = Now.AddSeconds(-1), updated_at = Now.AddSeconds(-1),
                user = new { id = 55, login = "maintainer", type = "User" }
            }));
            api.AfterPullRead = null;
        };
        using var client = api.Client();
        var messages = await new GitHubFeedbackSource(new(client, "owner/repo", 1),
            clock: new Clock(Now.AddMinutes(1))).ReadAsync(state, default);
        Assert.Single(messages, message => message.Data is HumanFeedback);
        Assert.Single(messages, message => message.Data is MergeFeedbackObserved);
        var reducer = new OrchestrationReducer(new Allow());
        foreach (var message in messages) state = reducer.Apply(state, message);

        Assert.Single(state.Issues[7].PendingFeedbackEventIds);
        Assert.Equal("error", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("unmerged")]
    [InlineData("timestamp")]
    [InlineData("read-failure")]
    public async Task PostMergeFeedbackReadMustCompleteAndRevalidateSameBoundary(string change)
    {
        var state = MergeReady();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        using var api = new Api { Merged = true };
        var reads = 0;
        api.BeforeComments = () =>
        {
            if (++reads != 2) return;
            switch (change)
            {
                case "head": api.Corruption = "head"; break;
                case "unmerged": api.Merged = false; break;
                case "timestamp": api.MergeTime = Now.AddSeconds(1); break;
                case "read-failure": api.ForbiddenSuffix = "/issues/17/comments"; break;
            }
        };
        using var client = api.Client();
        var source = new GitHubFeedbackSource(new(client, "owner/repo", 1), clock: new Clock(Now.AddMinutes(1)));
        if (change == "read-failure")
            await Assert.ThrowsAsync<HttpRequestException>(() => source.ReadAsync(state, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadAsync(state, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(-1)]
    public async Task SharedPullFeedbackHasPerIssueIdentityAndLegacyDeduplicationIsOwnerScoped(int legacyOwner)
    {
        var state = State();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        state.Issues[8] = StateJson.Clone(state).Issues[7];
        state.Issues[8].Number = 8;
        var updated = Now.AddSeconds(-1);
        if (legacyOwner != 0)
        {
            var legacy = new OrchestrationEvent($"feedback:1:1:17:1:{updated:O}", "owner/repo",
                legacyOwner == -1 ? 7 : legacyOwner, state.Generation, 1,
                new("maintainer", ActorKind.Human, 55), updated, new HumanFeedback("Shared PR instruction"));
            state.Inbox[legacy.Id] = new(legacy.Id, legacy.Digest, DeliveryStatus.Succeeded, updated, updated,
                legacyOwner == -1 ? null : StateJson.SerializeEvent(legacy));
        }
        using var api = new Api();
        api.PullComments.Add(JsonSerializer.SerializeToElement(new
        {
            id = 1, body = "Shared PR instruction", created_at = updated, updated_at = updated,
            user = new { id = 55, login = "maintainer", type = "User" }
        }));
        using var client = api.Client();
        var source = new GitHubFeedbackSource(new(client, "owner/repo", 1));
        var messages = await source.ReadAsync(state, default);
        Assert.Equal(legacyOwner > 0 ? 1 : 2, messages.Count);
        Assert.Equal(messages.Count, messages.Select(message => message.Id).Distinct().Count());
        Assert.All(messages, message =>
        {
            Assert.IsType<HumanFeedback>(message.Data);
            Assert.NotEqual(legacyOwner, message.IssueNumber);
        });
        var reducer = new OrchestrationReducer(new Allow());
        foreach (var message in messages) state = reducer.Apply(state, message);
        Assert.Empty(await source.ReadAsync(state, default));
    }

    [Fact]
    public async Task SharedPullProducesDistinctMergeFeedbackEvidenceForEachCanonicalIssue()
    {
        var state = MergeReady();
        state.Issues[7].ReadyAt = Now.AddMinutes(-1);
        state.Issues[8] = StateJson.Clone(state).Issues[7];
        state.Issues[8].Number = 8;
        using var api = new Api { Merged = true };
        using var client = api.Client();
        var source = new GitHubFeedbackSource(new(client, "owner/repo", 1), clock: new Clock());
        var messages = await source.ReadAsync(state, default);

        Assert.Equal(2, messages.Count);
        Assert.Equal(2, messages.Select(message => message.Id).Distinct().Count());
        var reducer = new OrchestrationReducer(new Allow());
        foreach (var message in messages) state = reducer.Apply(state, message);
        Assert.NotNull(state.Issues[7].MergeFeedback);
        Assert.NotNull(state.Issues[8].MergeFeedback);
        Assert.Empty(await source.ReadAsync(state, default));
    }

    [Fact]
    public async Task ErrorRevokesPublishedTestedHeadEvenIfLiveHeadHasMoved()
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = Planned();
        var success = Intent(state);
        state = reducer.RecordOutcome(state, success.Id, new(DeliveryStatus.Succeeded, "100"), Now);
        state.Issues[7].Phase = Phase.BLOCKED;
        state.Issues[7].HoldReason = "Link preflight observed changed live head.";
        var store = new Store(state);
        using var api = new Api { Corruption = "head" };
        using var client = api.Client();
        var result = await new OrchestrationReconciler().RevokeRequiredStatusAsync(store, 7, Adapter(client, store), Now);

        Assert.Equal(1, api.Posts);
        Assert.Contains("/statuses/" + Sha, api.Requests);
        Assert.Equal("error", Intent(result).RequiredStatus!.State);
        Assert.Equal(DeliveryStatus.Succeeded, Intent(result).Status);
        Assert.NotNull(Intent(result).RequiredStatusBoundary);
    }

    [Fact]
    public async Task DryRunCannotUseEarlyIndependentRevocationForUncertainLink()
    {
        var state = State();
        state.Issues[7].Phase = Phase.BLOCKED;
        state.Issues[7].HoldReason = "Link interrupted.";
        state.Outbox["link"] = new() { Id = "link", IssueNumber = 7, Generation = 1,
            Kind = EffectKind.LinkIssue, Status = DeliveryStatus.Unknown, Sha = Sha, IssueLink = new(1, 17) };
        var store = new Store(state);
        var before = StateJson.Serialize(state);
        var result = await new OrchestrationWorker().RunAsync(store, new EmptyEvents(), new Observations(),
            new ForbiddenEffects(), new(true, true, true, true));

        Assert.Equal(WorkerStopReason.DryRun, result.Reason);
        Assert.Equal(before, StateJson.Serialize(store.Value));
    }

    [Theory]
    [InlineData("closed-unmerged")]
    [InlineData("mapping")]
    [InlineData("head")]
    public void ClosedIssueCannotCarryApprovalAcrossUnmergedClosureOrConflictingMapping(string change)
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = MergeReady();
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestClosed = false;
        live.Issues[7].PullRequestMerged = false;
        state = reducer.Reconcile(state, live).State;
        Assert.NotNull(state.Issues[7].TerminalHistory.Last().Approval);
        switch (change)
        {
            case "closed-unmerged": live.Issues[7].PullRequestClosed = true; break;
            case "mapping": live.Issues[7].PullRequest = 18; break;
            case "head": live.Issues[7].HeadSha = Definition; break;
        }
        state = reducer.Reconcile(state, live).State;
        Assert.Null(state.Issues[7].TerminalHistory.Last().Approval);
        live.Issues[7].PullRequest = 17;
        live.Issues[7].HeadSha = Sha;
        live.Issues[7].PullRequestClosed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        state = reducer.Reconcile(state, live).State;

        Assert.Null(state.Issues[7].TerminalHistory.Last().Approval);
        Assert.Equal("error", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Fact]
    public async Task RecordedMergeCannotPublishSuccessIfLivePullWasClosedWithoutMerging()
    {
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = reducer.Reconcile(MergeReady(), live).State;
        var store = new Store(reducer.MarkExecuting(state, Intent(state).Id, Now));
        using var api = new Api { Corruption = "pull-closed" };
        using var client = api.Client();

        await Assert.ThrowsAsync<InvalidDataException>(() => Adapter(client, store).ExecuteAsync(Intent(store.Value), default));

        Assert.Equal(0, api.Posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedSuccessCannotOverwriteStatusForAnotherCanonicalOwner(bool samePull)
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var live = Live();
        live.Issues[7].Closed = true;
        live.Issues[7].PullRequestMerged = true;
        live.Issues[7].PullRequestMergedAt = Now;
        var state = reducer.Reconcile(MergeReady(), live).State;
        Assert.Equal("success", Intent(state).RequiredStatus!.State);
        state = reducer.MarkExecuting(state, Intent(state).Id, Now);
        state.Issues[8] = new() { Number = 8, Phase = Phase.BLOCKED,
            HeadSha = samePull ? Definition : Sha, PullRequest = samePull ? 17 : 18 };
        var store = new Store(state);
        using var api = new Api { Merged = true };
        using var client = api.Client();

        await Assert.ThrowsAsync<InvalidDataException>(() => Adapter(client, store).ExecuteAsync(Intent(store.Value), default));

        Assert.Equal(0, api.Posts);
        Assert.Equal("error", RequiredStatusPolicy.Desired(store.Value, store.Value.Issues[7]).State);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("feedback")]
    [InlineData("head")]
    [InlineData("handoff")]
    public void ClassificationHoldDoesNotMaskOtherBlockingConditions(string corruption)
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.HoldReason = "Review requires human classification.";
        issue.Review!.Classification = ReviewClassification.NeedsHuman;
        issue.Review.LifecycleState = ReviewLifecycleState.Completed;
        switch (corruption)
        {
            case "pause": issue.Paused = true; break;
            case "feedback": issue.PendingFeedbackEventIds.Add("feedback"); break;
            case "head": issue.HeadSha = Definition; break;
            case "handoff": issue.CurrentHandoffId = null; break;
        }

        Assert.Equal("error", RequiredStatusPolicy.Desired(state, issue).State);
    }

    [Fact]
    public void StatusPlanningIsOptInIdempotentAndFeedbackCreatesARevocationNotSuccess()
    {
        var state = State();
        Assert.DoesNotContain(new OrchestrationReducer().Reconcile(state, Live()).State.Outbox.Values,
            intent => intent.Kind == EffectKind.PublishRequiredStatus);
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        state = reducer.Reconcile(state, Live()).State;
        var success = Intent(state);
        Assert.Equal("success", success.RequiredStatus!.State);
        Assert.Single(reducer.Reconcile(state, Live()).State.Outbox.Values,
            intent => intent.Kind == EffectKind.PublishRequiredStatus);
        state.Issues[7].PendingFeedbackEventIds.Add("human");
        state = reducer.Reconcile(state, Live()).State;
        var revoked = Intent(state);
        Assert.Equal("error", revoked.RequiredStatus!.State);
        Assert.Equal(DeliveryStatus.Cancelled, state.Outbox[success.Id].Status);
        Assert.NotEqual(success.Id, revoked.Id);
        Assert.NotEqual(success.RequiredStatus.TargetUrl, revoked.RequiredStatus.TargetUrl);
        Assert.False(RequiredStatusPolicy.Matches(state, state.Issues[7], success));
    }

    [Fact]
    public void DeliveredStatusDoesNotAuthorizeAnyPhaseAndReturningToSamePayloadGetsNewIntent()
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = Planned();
        var first = Intent(state);
        state = reducer.RecordOutcome(state, first.Id, new(DeliveryStatus.Succeeded, "100"), Now);
        state.Issues[7].Paused = true;
        state = reducer.Reconcile(state, Live()).State;
        var error = Intent(state);
        Assert.Equal("error", error.RequiredStatus!.State);
        state = reducer.MarkExecuting(state, error.Id, Now);
        state = reducer.RecordOutcome(state, error.Id, new(DeliveryStatus.Succeeded, "101"), Now);
        Assert.Equal(Phase.HUMAN_REVIEW, state.Issues[7].Phase);
        Assert.True(state.Issues[7].Paused);
        state.Issues[7].Paused = false;
        state = reducer.Reconcile(state, Live()).State;
        var next = Intent(state);
        Assert.Equal("success", next.RequiredStatus!.State);
        Assert.NotEqual(first.Id, next.Id);
        Assert.NotEqual(first.RequiredStatus!.TargetUrl, next.RequiredStatus.TargetUrl);
        Assert.Equal(DeliveryStatus.Succeeded, state.Outbox[first.Id].Status);
    }

    [Fact]
    public async Task ActualReducerFinalHumanApprovalHoldCanPublishGreenCiWithoutClearingHold()
    {
        var store = new Store(Planned());
        var issue = store.Value.Issues[7];
        Assert.Equal(Phase.HUMAN_REVIEW, issue.Phase);
        Assert.False(issue.ActiveAuthorized);
        Assert.True(issue.ReactivationRequired);
        Assert.Equal("Awaiting human approval.", issue.HoldReason);
        Assert.NotNull(issue.CurrentHandoffId);
        var api = new Api();
        using var client = api.Client();
        var result = await Adapter(client, store).ExecuteAsync(Intent(store.Value), default);
        store.Value = new OrchestrationReducer().RecordOutcome(store.Value, Intent(store.Value).Id, result, Now);
        Assert.Equal("success", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.False(store.Value.Issues[7].ActiveAuthorized);
        Assert.True(store.Value.Issues[7].ReactivationRequired);
        Assert.Equal("Awaiting human approval.", store.Value.Issues[7].HoldReason);
    }

    [Fact]
    public async Task EligibleReviewPendingReportsGreenCiWithoutClassifyingReview()
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.REVIEW_PENDING;
        issue.ActiveAuthorized = true;
        issue.ReactivationRequired = false;
        issue.HoldReason = null;
        issue.Review!.Classification = null;
        issue.Review.LifecycleState = ReviewLifecycleState.Requested;
        state.AdmittedIssue = 7;
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        state = reducer.Reconcile(state, Live()).State;
        var store = new Store(reducer.MarkExecuting(state, Intent(state).Id, Now));
        var api = new Api();
        using var client = api.Client();
        Assert.Equal(DeliveryStatus.Succeeded, (await Adapter(client, store).ExecuteAsync(Intent(store.Value), default)).Status);
        Assert.Equal("success", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.Equal(Phase.REVIEW_PENDING, store.Value.Issues[7].Phase);
        Assert.Null(store.Value.Issues[7].Review!.Classification);
    }

    [Fact]
    public void IsolatedReducerPlansEligibleSuccessWithoutCreatingReviewWork()
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.REVIEW_PENDING;
        issue.ActiveAuthorized = true;
        issue.ReactivationRequired = false;
        issue.HoldReason = null;
        issue.Review = null;
        var reducer = new OrchestrationReducer(new Allow(), requiredStatusEnabled: true,
            reviewRequestsEnabled: false);

        var result = reducer.Reconcile(state, Live()).State;

        Assert.DoesNotContain(result.Outbox.Values, effect => effect.Kind == EffectKind.RequestReview);
        Assert.Equal("success", Intent(result).RequiredStatus!.State);
    }

    [Fact]
    public void ActualAcceptedReviewPreservesResolvedBlockerResponsesAndGreenCi()
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.REVIEW_PENDING;
        issue.ActiveAuthorized = true;
        issue.ReactivationRequired = false;
        issue.HoldReason = null;
        issue.Review!.Classification = null;
        issue.Review.LifecycleState = ReviewLifecycleState.Requested;
        issue.Review.Findings.Add(new("prior-blocker", "Needs fix", "specific prior evidence"));
        var response = new FindingResponse("prior-blocker", "Specific fix verified in accepted review", true);
        state = new OrchestrationReducer(new Allow()).Apply(state,
            new("accepted-fixed-review", "owner/repo", 7, 1, 1, new("reviewer", ActorKind.ReviewService), Now,
                new ReviewResult("review90", "task", 1, 17, Sha, ReviewClassification.Accepted, [], [response])));
        Assert.Contains(response, state.Issues[7].Review!.Responses);
        Assert.Equal("success", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
        state.Issues[7].Review!.Findings.Add(new("prior-blocker", "Retained resolved history", "prior evidence"));
        Assert.Equal("success", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
        state.Issues[7].Review!.Responses.Clear();
        Assert.NotEqual("success", RequiredStatusPolicy.Desired(state, state.Issues[7]).State);
    }

    [Fact]
    public async Task SuccessfulReceiptCannotBeReportedWhenHeadChangesAfterPost()
    {
        var store = new Store(Planned());
        var api = new Api();
        api.BeforePost = () => store.Value.Issues[7].HeadSha = Definition;
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        Assert.Equal(1, api.Posts);
        Assert.NotNull(Intent(store.Value).RequiredStatusBoundary!.StatusId);
        Assert.False(RequiredStatusPolicy.Matches(store.Value, store.Value.Issues[7], Intent(store.Value)));
        Assert.Equal(Phase.HUMAN_REVIEW, store.Value.Issues[7].Phase);
    }

    [Fact]
    public async Task ReceiptWithoutExactShaLookupEvidenceStaysUnknown()
    {
        var store = new Store(Planned());
        var api = new Api { HideCreatedStatus = true };
        using var client = api.Client();
        var adapter = Adapter(client, store);
        await Assert.ThrowsAsync<InvalidDataException>(() => adapter.ExecuteAsync(Intent(store.Value), default));
        Assert.Equal(DeliveryStatus.Unknown, (await adapter.ReconcileAsync(Intent(store.Value), default)).Status);
        Assert.Equal(1, api.Posts);
    }

    [Fact]
    public async Task CompletedHeadDriftPlansErrorOnValidatedNewHeadAndPreservesAllEligibilityEvidence()
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        var oldSuccess = Intent(state);
        var live = Live();
        live.Issues[7].HeadSha = Definition;
        state = reducer.Reconcile(state, live).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(Definition, state.Issues[7].HeadSha);
        Assert.Equal(Sha, state.Issues[7].Tasks["task"].FinalSha);
        Assert.Equal(Sha, state.Issues[7].Ci!.Sha);
        Assert.Equal(Sha, state.Issues[7].Review!.Sha);
        Assert.Equal(ReviewClassification.Accepted, state.Issues[7].Review!.Classification);
        Assert.Equal(DeliveryStatus.Cancelled, state.Outbox[oldSuccess.Id].Status);
        Assert.Equal(Definition, Intent(state).Sha);
        Assert.Equal("error", Intent(state).RequiredStatus!.State);
        var store = new Store(reducer.MarkExecuting(state, Intent(state).Id, Now));
        var api = new Api { Corruption = "head" };
        using var client = api.Client();
        Assert.Equal(DeliveryStatus.Succeeded, (await Adapter(client, store).ExecuteAsync(Intent(store.Value), default)).Status);
        Assert.Contains($"/statuses/{Definition}", api.Requests);
        Assert.DoesNotContain($"/statuses/{Sha}", api.Requests);
        Assert.Equal("error", JsonDocument.Parse(api.Body!).RootElement.GetProperty("state").GetString());
        Assert.Equal(Phase.BLOCKED, store.Value.Issues[7].Phase);
    }

    [Fact]
    public void CodingPushUpdatesObservedHeadWithoutCompletingTaskOrDispatchingCiReview()
    {
        var state = State();
        var issue = state.Issues[7];
        issue.Phase = Phase.CODING;
        issue.ActiveAuthorized = true;
        issue.ReactivationRequired = false;
        issue.HoldReason = null;
        issue.Tasks["task"].Status = TaskStatus.Running;
        issue.Tasks["task"].SourceSha = Sha;
        issue.Tasks["task"].FinalSha = null;
        state.AdmittedIssue = 7;
        var live = Live();
        live.Issues[7].HeadSha = Definition;
        var result = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(state, live).State;
        Assert.Equal(Definition, result.Issues[7].HeadSha);
        Assert.Equal(Phase.CODING, result.Issues[7].Phase);
        Assert.Equal(TaskStatus.Running, result.Issues[7].Tasks["task"].Status);
        Assert.Equal(Sha, result.Issues[7].Tasks["task"].SourceSha);
        Assert.Null(result.Issues[7].Tasks["task"].FinalSha);
        Assert.Equal(Sha, result.Issues[7].Ci!.Sha);
        Assert.DoesNotContain(result.Outbox.Values,
            intent => intent.Status == DeliveryStatus.Pending && intent.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        Assert.Equal(Definition, Intent(result).Sha);
        Assert.Equal("pending", Intent(result).RequiredStatus!.State);
    }

    [Fact]
    public void ChangedMappingCannotReplaceCanonicalHeadOrAuthorizeSuccess()
    {
        var state = State();
        var live = Live();
        live.Issues[7].PullRequest = 18;
        live.Issues[7].HeadSha = Definition;
        state = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(state, live).State;
        Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
        Assert.Equal(17, state.Issues[7].PullRequest);
        Assert.Equal(Sha, state.Issues[7].HeadSha);
        Assert.Equal("error", Intent(state).RequiredStatus!.State);
    }

    [Theory]
    [InlineData(DeliveryStatus.Pending)]
    [InlineData(DeliveryStatus.Executing)]
    [InlineData(DeliveryStatus.Unknown)]
    public void StatusProjectionDoesNotConsumeAdmissionOrBlockAnotherReadyIssue(DeliveryStatus delivery)
    {
        var state = Planned();
        var originalId = Intent(state).Id;
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        state.AdmittedIssue = 7;
        if (delivery == DeliveryStatus.Unknown)
            state = reducer.RecordOutcome(state, originalId, new(DeliveryStatus.Unknown), Now);
        else state.Outbox[originalId].Status = delivery;
        state.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        var live = Live();
        live.Issues[8] = new() { Ready = true };
        state = reducer.Reconcile(state, live).State;
        Assert.Equal(8, state.AdmittedIssue);
        Assert.Equal(Phase.CODING, state.Issues[8].Phase);
        Assert.Single(state.Outbox.Values, effect => effect.IssueNumber == 8 && effect.Kind == EffectKind.LaunchTask);
        Assert.Equal(delivery, state.Outbox[originalId].Status);
        Assert.Equal(DeliveryStatus.Succeeded, state.Outbox["ci"].Status);
        Assert.Equal("90", state.Outbox["ci"].ExternalId);
        if (delivery == DeliveryStatus.Unknown)
        {
            Assert.Equal(Phase.BLOCKED, state.Issues[7].Phase);
            Assert.True(state.Issues[7].ReactivationRequired);
            Assert.False(state.Issues[7].ActiveAuthorized);
            Assert.Equal("error", Intent(state).RequiredStatus!.State);
        }
    }

    [Fact]
    public void ActualTaskUncertaintyStillPreservesAdmissionAndBlocksOtherReadyIssues()
    {
        var state = State();
        state.AdmittedIssue = 7;
        var issue = state.Issues[7];
        issue.Phase = Phase.BLOCKED;
        issue.ActiveTaskId = "uncertain-task";
        issue.Tasks["uncertain-task"] = new() { Id = "uncertain-task", Round = 1, Status = TaskStatus.Unknown,
            ExternalId = "uncertain-remote-task", SourceSha = Sha, LaunchSource = TaskLaunchSource.PullRequestHead };
        state.Outbox["uncertain-launch"] = new() { Id = "uncertain-launch", Digest = "fixture",
            IssueNumber = 7, Generation = 1, Kind = EffectKind.LaunchTask, TaskId = "uncertain-task",
            Round = 1, Sha = Sha, Status = DeliveryStatus.Unknown, ExternalId = "uncertain-remote-task" };
        state.Issues[8] = new() { Number = 8, Ready = true, ReadyAt = Now };
        var live = Live();
        live.Issues[8] = new() { Ready = true };
        state = new OrchestrationReducer(requiredStatusEnabled: true).Reconcile(state, live).State;
        Assert.Equal(7, state.AdmittedIssue);
        Assert.Equal(Phase.READY, state.Issues[8].Phase);
        Assert.Empty(state.Issues[8].Tasks);
        Assert.Equal(DeliveryStatus.Unknown, state.Outbox["uncertain-launch"].Status);
        Assert.Equal("uncertain-remote-task", state.Outbox["uncertain-launch"].ExternalId);
    }

    [Theory]
    [InlineData("ci-pending")]
    [InlineData("review-pending")]
    [InlineData("stopped")]
    [InlineData("unconfirmed")]
    [InlineData("stale-run")]
    [InlineData("wrong-task")]
    [InlineData("blocker")]
    [InlineData("dispatch-generation")]
    [InlineData("dispatch-issue")]
    public void GreenOrStaleCiCannotBypassCompletionReviewAndHoldGuards(string corruption)
    {
        var state = State();
        var issue = state.Issues[7];
        switch (corruption)
        {
            case "ci-pending": issue.Phase = Phase.CI_PENDING; break;
            case "review-pending": issue.Phase = Phase.REVIEW_PENDING; break;
            case "stopped": issue.Ci!.TerminalStopReason = "stopped"; break;
            case "unconfirmed": issue.Tasks["task"].LaunchConfirmed = false; break;
            case "stale-run": issue.Ci!.RunId = "91"; break;
            case "wrong-task": issue.Ci!.TaskId = "unrelated"; break;
            case "blocker": issue.Review!.Findings.Add(new("blocker", "Needs fix", "evidence")); break;
            case "dispatch-generation": state.Outbox["ci"].Generation++; break;
            case "dispatch-issue": state.Outbox["ci"].IssueNumber++; break;
        }
        Assert.NotEqual("success", RequiredStatusPolicy.Desired(state, issue).State);
    }

    private static GitHubRequiredStatusAdapter Adapter(HttpClient client, Store store) =>
        new(new(client, "owner/repo", 1), store, Publisher, Approved, new Clock());
    private static EffectIntent Intent(RepositoryState state) => state.Outbox[state.Issues[7].RequiredStatusIntentId!];
    private static RepositoryState Planned()
    {
        var reducer = new OrchestrationReducer(requiredStatusEnabled: true);
        var state = reducer.Reconcile(State(), Live()).State;
        return reducer.MarkExecuting(state, Intent(state).Id, Now);
    }
    private static LiveObservation Live() => new() { ObservedAt = Now,
        Issues = new() { [7] = new() { PullRequest = 17, HeadSha = Sha, Ready = true, Labels = ["agent:review"] } } };
    private static RepositoryState MergeReady()
    {
        var state = new OrchestrationReducer().ConfirmFinalApprovals(State(), Now.AddSeconds(-1));
        state.Issues[7].MergeFeedback = new(1, 17, Sha, Now, Now);
        return state;
    }

    private static RepositoryState State()
    {
        var state = RepositoryState.Create("owner/repo");
        state.AdmittedIssue = 7;
        state.Issues[7] = new() { Number = 7, Phase = Phase.REVIEW_PENDING, ActiveAuthorized = true,
            PullRequest = 17, HeadSha = Sha, Round = 1, ActiveTaskId = "task",
            Tasks = new() { ["task"] = new() { Id = "task", Status = TaskStatus.Completed, LaunchConfirmed = true,
            ExternalId = "remote-task", Round = 1, FinalSha = Sha,
            CandidateShaEvidence = new(TaskTerminalRecovery.CandidateShaEvidenceVersion,
                TaskTerminalRecovery.CandidateShaPolicy, "task", "remote-task", ["session"], 17, Sha) } },
            Ci = new() { IntentId = "ci", TaskId = "task", Round = 1, Sha = Sha, Green = true, RunId = "90",
                DispatchBoundary = new(10, Definition, "refs/tags/trusted", "managed-correlation", Now, 90) },
            Review = new() { ReviewId = "review90", TaskId = "task", Round = 1, Sha = Sha } };
        state.Outbox["ci"] = new() { Id = "ci", Digest = "fixture", Kind = EffectKind.RunCi, Status = DeliveryStatus.Succeeded,
            IssueNumber = 7, Generation = 1, TaskId = "task", Sha = Sha, Round = 1, ExternalId = "90" };
        return new OrchestrationReducer(new Allow()).Apply(state,
            new("accepted-review", "owner/repo", 7, 1, 1, new("reviewer", ActorKind.ReviewService), Now,
                new ReviewResult("review90", "task", 1, 17, Sha, ReviewClassification.Accepted, [], [])));
    }

    private sealed class Allow : IAuthorizationPolicy { public bool IsAuthorized(OrchestrationEvent message) => true; }
    private sealed class Clock(DateTimeOffset? now = null) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now ?? Now;
    }
    private sealed class Observations(LiveObservation? observation = null) : IObservationSource
    {
        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken token) => Task.FromResult(observation ?? Live());
    }
    private sealed class FeedbackEvents : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token) => Task.FromResult(
            new WorkerEventBatch(state.Inbox.ContainsKey("feedback-after-loss") ? [] :
                [new("feedback-after-loss", "owner/repo", 7, 1, 1, new("human", ActorKind.Human), Now,
                    new HumanFeedback("Please inspect this human feedback before doing more work."))]));
    }
    private sealed class EmptyEvents : IWorkerEventSource
    {
        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token) =>
            Task.FromResult(new WorkerEventBatch([]));
    }
    private sealed class ForbiddenEffects : IEffectAdapter, IIndependentStatusRevocation
    {
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token) =>
            throw new InvalidOperationException("Dry run cannot execute.");
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) =>
            throw new InvalidOperationException("Dry run cannot recover external effects.");
        public Task<RepositoryState> RevokeRequiredStatusesAsync(IStateStore store, ReconcileOptions options,
            DateTimeOffset now, CancellationToken token) =>
            throw new InvalidOperationException("Dry run cannot revoke.");
    }

    private sealed class RecordingEffects(GitHubRequiredStatusAdapter adapter) : IEffectAdapter
    {
        public List<EffectIntent> Executed { get; } = [];
        public Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken token)
        {
            Executed.Add(intent);
            return intent.Kind == EffectKind.PublishRequiredStatus ? adapter.ExecuteAsync(intent, token) :
                intent.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus or EffectKind.LinkIssue
                    ? Task.FromResult(new EffectOutcome(DeliveryStatus.Succeeded, "offline-projection")) :
                    throw new InvalidOperationException("New automated work must not execute.");
        }
        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken token) => adapter.ReconcileAsync(intent, token);
    }
    private sealed class Store(RepositoryState state) : IStateStore
    {
        public RepositoryState Value { get; set; } = state;
        public bool FailSave { get; set; }
        public Func<RepositoryState, bool>? RejectSave { get; set; }
        public Task<StateSnapshot> LoadAsync(CancellationToken token) => Task.FromResult(new StateSnapshot(
            Value.Version.ToString(), StateJson.Deserialize(StateJson.Serialize(Value))));
        public Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState next, CancellationToken token)
        {
            if (FailSave) throw new IOException("offline persistence failure");
            if (RejectSave?.Invoke(next) == true) return Task.FromResult(false);
            if (expected.Revision != Value.Version.ToString()) return Task.FromResult(false);
            Value = next;
            return Task.FromResult(true);
        }
    }

    private sealed class Api : HttpMessageHandler
    {
        public List<JsonElement> IssueComments { get; } = [];
        public List<JsonElement> PullComments { get; } = [];
        public List<string> Requests { get; } = [];
        public int Posts { get; private set; }
        public string? Body { get; private set; }
        public string? Corruption { get; set; }
        public string? ForbiddenSuffix { get; set; }
        public bool LostResponse { get; set; }
        public bool Merged { get; set; }
        public DateTimeOffset MergeTime { get; set; } = Now;
        public bool CancelAfterPost { get; set; }
        public bool SynchronizePreparation { get; set; }
        public bool HideCreatedStatus { get; set; }
        public bool HideFirstStatus { get; set; }
        public Action? BeforePost { get; set; }
        public Func<Task>? BeforePostAsync { get; set; }
        public Action? AfterPullRead { get; set; }
        public Action? BeforeComments { get; set; }
        public Func<Task>? OnPullRead { get; set; }
        private object? created;
        private readonly List<(string Body, string Sha)> createdStatuses = [];
        private readonly TaskCompletionSource preparation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int preparationReads;
        public HttpClient Client() => new(this, false) { BaseAddress = new("https://api.github.com/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(path.StartsWith("/repos/owner/repo", StringComparison.Ordinal)
                ? path["/repos/owner/repo".Length..] : path);
            if (request.Method == HttpMethod.Get && ForbiddenSuffix is { } suffix &&
                path.EndsWith(suffix, StringComparison.Ordinal))
                return new(HttpStatusCode.Forbidden) { Content = new StringContent("private API response") };
            object value;
            if (request.Method == HttpMethod.Post)
            {
                BeforePost?.Invoke();
                if (BeforePostAsync is not null) await BeforePostAsync();
                Posts++;
                Body = await request.Content!.ReadAsStringAsync(token);
                var sha = path[(path.LastIndexOf('/') + 1)..];
                createdStatuses.Add((Body, sha));
                using var body = JsonDocument.Parse(Body);
                created = Status(body.RootElement, sha, 99 + Posts);
                if (LostResponse) throw new HttpRequestException("offline lost response");
                if (CancelAfterPost) throw new OperationCanceledException("offline cancellation after create");
                value = created;
            }
            else if (path == "/repos/owner/repo") value = new { id = 1, full_name = "owner/repo" };
            else if (path.EndsWith("/actions/runs/44", StringComparison.Ordinal))
                value = new { @event = "workflow_dispatch", path = ".github/workflows/agent-state-machine.yml",
                    actor = new { id = 55 } };
            else if (path == "/users/maintainer") value = new { id = 55, login = "maintainer", type = "User" };
            else if (path.EndsWith("/collaborators/maintainer/permission", StringComparison.Ordinal))
                value = new { permission = "write", user = new { id = 55, login = "maintainer", type = "User" } };
            else if (path.StartsWith("/users/", StringComparison.Ordinal)) value = Actor();
            else if (path.EndsWith("/issues/7", StringComparison.Ordinal))
                value = new { id = 70, number = 7, state = Corruption == "issue-closed" ? "closed" : "open",
                    labels = Array.Empty<object>() };
            else if (path.EndsWith("/issues/7/comments", StringComparison.Ordinal))
            {
                BeforeComments?.Invoke();
                value = IssueComments;
            }
            else if (path.EndsWith("/issues/8/comments", StringComparison.Ordinal)) value = Array.Empty<object>();
            else if (path.EndsWith("/issues/17/comments", StringComparison.Ordinal)) value = PullComments;
            else if (path.EndsWith("/pulls/17", StringComparison.Ordinal))
            {
                value = new { number = 17, state = Merged || Corruption == "pull-closed" ? "closed" : "open", merged = Merged,
                    merged_at = Merged ? MergeTime : (DateTimeOffset?)null,
                    @base = new { repo = new { id = 1 } },
                    head = new { sha = Corruption == "head" ? Definition : Sha, repo = new { id = 1 } } };
                AfterPullRead?.Invoke();
                if (OnPullRead is not null) await OnPullRead();
            }
            else if (path.Contains("/commits/refs", StringComparison.Ordinal))
                value = new { sha = Corruption == "definition" ? Sha : Definition };
            else if (path.EndsWith("/actions/runs/90", StringComparison.Ordinal))
                value = new { id = 90, workflow_id = 10, repository = new { id = Corruption == "run-repository" ? 2 : 1 },
                    path = Corruption == "run-path" ? "wrong.yml" : GitHubCiAdapter.WorkflowPath,
                    @event = "workflow_dispatch", head_sha = Corruption == "run-sha" ? Sha : Definition,
                    run_attempt = Corruption == "run-attempt" ? 2 : 1, display_title = "managed-correlation",
                    status = "completed", conclusion = Corruption == "run-conclusion" ? "cancelled" : "success" };
            else if (path.EndsWith("/jobs", StringComparison.Ordinal))
                value = new { total_count = Corruption == "job-count" ? 2 : 1,
                    jobs = new[] { new { id = 20, run_id = 90, run_attempt = 1,
                        head_sha = Corruption == "job-sha" ? Sha : Definition,
                        name = "Test " + (Corruption == "job-name" ? Definition : Sha), status = "completed", conclusion = "success",
                        steps = new[] { "Verify tested SHA", "Restore dependencies", "Build solution", "Run tests" }
                            .Select(name => new { name, status = "completed", conclusion = Corruption == "step" && name == "Run tests" ? "failure" : "success" }) } } };
            else if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                if (SynchronizePreparation)
                {
                    if (Interlocked.Increment(ref preparationReads) == 2) preparation.SetResult();
                    await preparation.Task.WaitAsync(token);
                }
                value = new { sha = Corruption == "lookup-sha" ? Definition : path.Split('/')[5],
                    repository = new { id = 1, full_name = "owner/repo" } };
            }
            else if (path.EndsWith("/statuses", StringComparison.Ordinal))
            {
                if (created is null || HideCreatedStatus) value = Array.Empty<object>();
                else
                {
                    var statuses = new List<object>();
                    for (var index = createdStatuses.Count - 1; index >= 0; index--)
                    {
                        if (createdStatuses[index].Sha != path.Split('/')[5] || HideFirstStatus && index == 0) continue;
                        using var body = JsonDocument.Parse(createdStatuses[index].Body);
                        statuses.Add(Status(body.RootElement, createdStatuses[index].Sha, 100 + index));
                        if (Corruption == "duplicate") statuses.Add(Status(body.RootElement, createdStatuses[index].Sha, 1000 + index));
                    }
                    value = statuses;
                }
            }
            else throw new InvalidOperationException("Unexpected offline request: " + path);
            return new(request.Method == HttpMethod.Post ? HttpStatusCode.Created : HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        }
        private object Actor() => new { id = Corruption == "actor" ? 10 : 9, login = "trusted[bot]",
            node_id = Corruption == "node" ? "BOT_10" : "BOT_9", type = Corruption == "actor-type" ? "User" : "Bot" };
        private object Status(JsonElement body, string sha, int id = 100) => new { id,
            url = $"https://api.github.com/repos/{(Corruption == "repository" ? "other/repo" : "owner/repo")}/statuses/{(Corruption == "status-id-url" ? id.ToString() : Corruption == "status-sha" ? (sha == Sha ? Definition : Sha) : sha)}",
            creator = Actor(), context = Corruption == "context" ? "untrusted" : body.GetProperty("context").GetString(),
            state = Corruption == "state" ? "failure" : body.GetProperty("state").GetString(),
            target_url = Corruption == "target" ? "https://example.invalid/" : body.GetProperty("target_url").GetString(),
            description = Corruption == "description" ? "other" : body.GetProperty("description").GetString(),
            created_at = (Corruption == "created" ? Now.AddDays(-1) : Corruption == "future" ? Now.AddDays(1) : Now)
                .ToString("yyyy-MM-ddTHH:mm:ssZ") };
    }
}
