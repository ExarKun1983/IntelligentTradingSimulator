using IntelligentTradingSimulator.Orchestration;
using TaskStatus = IntelligentTradingSimulator.Orchestration.TaskStatus;

namespace IntelligentTradingSimulator.Tests.Orchestration;

public sealed class OrchestrationWorkerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static readonly ReconcileOptions Simulation = new(false, true, true, true, AllowOfflineAgentSimulation: true);
    private static readonly WorkerOptions Fast = new() { MaximumIterations = 64, PollInterval = TimeSpan.Zero };

    [Fact]
    public async Task WorkerAutomaticallyRunsTwoCiRepairsAndReviewRepairThenSettlesWithoutManualStages()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CiFailures = 2, ReviewBlockers = 1 };
        var result = await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Equal(WorkerStopReason.Settled, result.Reason);
        var issue = store.Current.Issues[7];
        Assert.Equal(Phase.HUMAN_REVIEW, issue.Phase);
        Assert.Null(store.Current.AdmittedIssue);
        Assert.Equal(new[] { TaskKind.Coding, TaskKind.CiRepair, TaskKind.CiRepair, TaskKind.ReviewRepair },
            issue.Tasks.Values.Select(task => task.Kind));
        Assert.Equal(4, api.Executions.Count(kind => kind == EffectKind.LaunchTask));
        Assert.Equal(4, api.Executions.Count(kind => kind == EffectKind.RunCi));
        Assert.Equal(2, api.Executions.Count(kind => kind == EffectKind.RequestReview));
        Assert.Equal(0, issue.ConsecutiveCiFailures);
        Assert.All(issue.Tasks.Values.Where(task => task.Kind == TaskKind.CiRepair),
            task => Assert.Equal(1, task.Round));
        Assert.All(store.Current.Inbox.Values, entry => Assert.NotNull(entry.Event));
        Assert.DoesNotContain(store.Current.Outbox.Values,
            effect => effect.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown);

        var count = api.Executions.Count;
        var resumed = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.Settled, resumed.Reason);
        Assert.Equal(count, api.Executions.Count);
    }

    [Fact]
    public async Task SettledIssueHandoffIsPublishedWhileNextIssueKeepsAdmission()
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], TrackProjectedLabels = true };
        api.AfterExecute = () =>
        {
            if (api.LaunchedIssues.Contains(8)) api.CompleteTasks = false;
        };

        await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        var first = store.Current.Issues[7];
        Assert.Equal(Phase.HUMAN_REVIEW, first.Phase);
        Assert.Equal(8, store.Current.AdmittedIssue);
        Assert.Contains("human:review", api.ProjectedLabels[7]);
        Assert.Contains(api.Deliveries, delivery => delivery.Issue == 7 &&
            delivery.Kind == EffectKind.ProjectLabels && delivery.Admission == 8);
        Assert.Contains(api.Deliveries, delivery => delivery.Issue == 7 &&
            delivery.Kind == EffectKind.ProjectStatus && delivery.Admission == 8 &&
            delivery.StatusBody == StatusProjection.Render(first));
        Assert.Single(store.Current.Issues[8].Tasks);
    }

    [Fact]
    public async Task UnmappedIssueLaunchesOnceAndWaitsForCompletionAndVerifiedFinalMapping()
    {
        var store = new Store();
        var api = new OfflineApi(store) { NoInitialPullRequest = true, CompleteTasks = false };
        var bounded = Fast with { MaximumIterations = 3 };
        await Worker().RunAsync(store, api, api, api, Simulation, bounded);
        var issue = store.Current.Issues[7];
        var task = issue.Tasks[issue.ActiveTaskId!];
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(TaskLaunchSource.UnmappedIssue, task.LaunchSource);
        Assert.Null(task.SourceSha);
        Assert.Null(issue.PullRequest);
        Assert.Null(issue.HeadSha);
        Assert.True(task.LaunchConfirmed);
        Assert.NotNull(task.ExternalId);

        api.Backlog.Enqueue(new([
            new("ready-again", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human), Now.AddMinutes(1), new Ready()),
            new("resume", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human), Now.AddMinutes(1), new Resume())
        ]));
        await Worker().RunAsync(store, api, api, api, Simulation, bounded);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Single(store.Current.Issues[7].Tasks);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Equal(Phase.CODING, store.Current.Issues[7].Phase);
        Assert.DoesNotContain(store.Current.Outbox.Values,
            effect => effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);

        api.CompleteTasks = true;
        // One iteration accepts completion, verifies live mapping, then dispatches exact-final-SHA CI.
        await Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumIterations = 1 });
        issue = store.Current.Issues[7];
        Assert.Equal(Phase.CI_PENDING, issue.Phase);
        Assert.Equal(17, issue.PullRequest);
        Assert.Equal(TaskStatus.Completed, issue.Tasks[task.Id].Status);
        Assert.Equal(issue.Tasks[task.Id].FinalSha, issue.HeadSha);
        Assert.Equal(issue.HeadSha, issue.Ci!.Sha);
        Assert.Equal(task.Id, issue.Ci.TaskId);
        Assert.Single(api.Executions, kind => kind == EffectKind.RunCi);
        Assert.DoesNotContain(EffectKind.RequestReview, api.Executions);
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Theory]
    [InlineData("pr")]
    [InlineData("head")]
    [InlineData("task")]
    public async Task UnmappedCompletionCannotDispatchCiWithoutMatchingLivePrHeadAndTask(string mismatch)
    {
        var store = new Store();
        var api = new OfflineApi(store) { NoInitialPullRequest = true, CompleteTasks = false };
        await Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumIterations = 2 });
        api.CompletionMismatch = mismatch;
        api.CompleteTasks = true;
        var result = await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Equal(WorkerStopReason.Blocked, result.Reason);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(EffectKind.RunCi, api.Executions);
        Assert.DoesNotContain(EffectKind.RequestReview, api.Executions);
        Assert.Null(store.Current.Issues[7].Ci);
    }

    [Fact]
    public async Task YieldAndDuplicateResumeKeepTheSameTaskAndDispatchNoEarlyCiOrReview()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var bounded = Fast with { MaximumIterations = 3 };
        var first = await Worker().RunAsync(store, api, api, api, Simulation, bounded);
        var task = store.Current.Issues[7].ActiveTaskId;
        var second = await Worker().RunAsync(store, api, api, api, Simulation, bounded);

        Assert.Equal(WorkerStopReason.IterationLimit, first.Reason);
        Assert.Equal(WorkerStopReason.IterationLimit, second.Reason);
        Assert.Equal(3, second.Iterations);
        Assert.Equal(task, store.Current.Issues[7].ActiveTaskId);
        Assert.Equal(Phase.CODING, store.Current.Issues[7].Phase);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(EffectKind.RunCi, api.Executions);
        Assert.DoesNotContain(EffectKind.RequestReview, api.Executions);

        api.CompleteTasks = true;
        Assert.Equal(WorkerStopReason.Settled,
            (await Worker().RunAsync(store, api, api, api, Simulation, Fast)).Reason);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Single(api.Executions, kind => kind == EffectKind.RunCi);
        Assert.Single(api.Executions, kind => kind == EffectKind.RequestReview);
    }

    [Fact]
    public async Task AutomaticContinuationStopsAfterThreeResumptionsAndHoldsTheTask()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        var worker = Worker();

        var result = await new AutomaticWorkerContinuation(store).RunAsync(
            token => worker.RunAsync(store, api, api, api, Simulation, options, token));

        var issue = store.Current.Issues[7];
        var task = issue.Tasks[issue.ActiveTaskId!];
        Assert.Equal(WorkerStopReason.ContinuationLimit, result.Reason);
        Assert.Equal(3, task.AutomaticContinuations);
        Assert.False(task.ContinuationPending);
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.Equal("Automatic continuation limit reached.", issue.HoldReason);
        Assert.True(issue.ReactivationRequired);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.DoesNotContain(EffectKind.RunCi, api.Executions);
    }

    [Fact]
    public async Task OtherWakesCannotResetOrBypassContinuationBudget()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        for (var iteration = 0; iteration < 4; iteration++)
        {
            await Worker().RunAsync(store, api, api, api, Simulation, options);
            var issue = store.Current.Issues[7];
            var task = issue.Tasks[issue.ActiveTaskId!];
            Assert.Equal(iteration, task.AutomaticContinuations);
            Assert.Equal(iteration < 3, task.ContinuationPending);
        }

        var before = store.Current.Issues[7];
        Assert.Equal(Phase.BLOCKED, before.Phase);
        await Worker().RunAsync(store, api, api, api, Simulation, options);
        var after = store.Current.Issues[7];
        Assert.Equal(Phase.BLOCKED, after.Phase);
        Assert.Equal(3, after.Tasks[after.ActiveTaskId!].AutomaticContinuations);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Fact]
    public async Task CompetingWakesConsumeOnlyOneContinuationWhileLeaseIsHeld()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        await Worker().RunAsync(store, api, api, api, Simulation, options);
        var blocked = new BlockingSource();
        var running = Worker().RunAsync(store, blocked, api, api, Simulation, options);
        await blocked.Started.Task;
        try
        {
            var concurrent = await Worker().RunAsync(store, api, api, api, Simulation, options);
            Assert.Equal(WorkerStopReason.LeaseHeld, concurrent.Reason);
            var issue = store.Current.Issues[7];
            Assert.Equal(1, issue.Tasks[issue.ActiveTaskId!].AutomaticContinuations);
        }
        finally
        {
            blocked.Release.TrySetResult();
            await running;
        }
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Fact]
    public async Task ExpiredLeaseRecoversPendingContinuationWithoutDuplicatingTask()
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        await Worker(clock).RunAsync(store, api, api, api, Simulation, options);
        var snapshot = await store.LoadAsync(CancellationToken.None);
        snapshot.State.WorkerLease = new("crashed", Now - TimeSpan.FromSeconds(1));
        Assert.True(await store.TrySaveAsync(snapshot, snapshot.State, CancellationToken.None));

        await Worker(clock).RunAsync(store, api, api, api, Simulation, options);

        var issue = store.Current.Issues[7];
        var task = issue.Tasks[issue.ActiveTaskId!];
        Assert.Equal(1, task.AutomaticContinuations);
        Assert.True(task.ContinuationPending);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Null(store.Current.WorkerLease);
    }

    [Theory]
    [InlineData(0, WorkerStopReason.IterationLimit)]
    [InlineData(3, WorkerStopReason.ContinuationLimit)]
    public async Task ExpiredLeaseCountsInterruptedWorkerAndEnforcesItsExistingBudget(
        int used, WorkerStopReason expected)
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        await Worker(clock).RunAsync(store, api, api, api, Simulation, options);
        var snapshot = await store.LoadAsync(CancellationToken.None);
        var issue = snapshot.State.Issues[7];
        var task = issue.Tasks[issue.ActiveTaskId!];
        task.AutomaticContinuations = used;
        task.ContinuationPending = false;
        snapshot.State.WorkerLease = new("crashed", Now - TimeSpan.FromSeconds(1));
        Assert.True(await store.TrySaveAsync(snapshot, snapshot.State, CancellationToken.None));

        var result = await Worker(clock).RunAsync(store, api, api, api, Simulation, options);

        issue = store.Current.Issues[7];
        Assert.Equal(expected, result.Reason);
        Assert.Equal(used == 3 ? 3 : 1, issue.Tasks[issue.ActiveTaskId!].AutomaticContinuations);
        Assert.Equal(used == 3 ? Phase.BLOCKED : Phase.CODING, issue.Phase);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Fact]
    public async Task InterruptedWorkerRecordsContinuationForLaterWakeWithoutRepeatingLaunch()
    {
        var store = new Store();
        using var cancellation = new CancellationTokenSource();
        var api = new OfflineApi(store) { AfterExecute = cancellation.Cancel, CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, options, cancellation.Token));
        var issue = store.Current.Issues[7];
        Assert.True(issue.Tasks[issue.ActiveTaskId!].ContinuationPending);
        api.AfterExecute = null;

        await Worker().RunAsync(store, api, api, api, Simulation, options);

        issue = store.Current.Issues[7];
        Assert.Equal(1, issue.Tasks[issue.ActiveTaskId!].AutomaticContinuations);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(1, api.Recoveries);
    }

    [Fact]
    public async Task PauseIsPersistedWhileWorkerIsActiveAndPreventsFurtherContinuation()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        await Worker().RunAsync(store, api, api, api, Simulation, options);
        var blocked = new BlockingSource();
        var running = Worker().RunAsync(store, blocked, api, api, Simulation, options);
        await blocked.Started.Task;
        try
        {
            await new OrchestrationReconciler(new OrchestrationReducer(new Authorized())).DispatchAsync(store,
                new("pause", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human), Now, new Pause("Stop.")));
            Assert.True(store.Current.Issues[7].Paused);
        }
        finally
        {
            blocked.Release.TrySetResult();
            await running;
        }
        var issue = store.Current.Issues[7];
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.Equal("Stop.", issue.HoldReason);
        Assert.False(issue.Tasks[issue.ActiveTaskId!].ContinuationPending);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Fact]
    public async Task AutomaticWakeWaitsForLeaseInsteadOfDroppingItsObservation()
    {
        var clock = new Clock();
        var calls = 0;
        var store = new Store();
        var continuation = new AutomaticWorkerContinuation(store, clock).RunAsync(_ =>
            Task.FromResult(new WorkerResult(++calls == 1 ? WorkerStopReason.LeaseHeld : WorkerStopReason.Settled, 0, null)));
        Assert.False(continuation.IsCompleted);
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, store.Reads);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkerStopReason.Settled, (await continuation).Reason);
        Assert.Equal(2, calls);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public void LeasePollingUsesBoundedJitteredBackoffAndRespectsRemainingTime()
    {
        var elapsed = TimeSpan.Zero;
        var retries = 0;
        while (elapsed < TimeSpan.FromMinutes(30))
        {
            var delay = LeaseWait.NextDelay(retries++, LeaseWait.Limit - elapsed);
            Assert.InRange(delay, TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(2));
            elapsed += delay;
        }
        Assert.InRange(retries, 1, 25);
        Assert.Equal(TimeSpan.FromMilliseconds(1), LeaseWait.NextDelay(20, TimeSpan.FromMilliseconds(1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseWaitLoopsStayBoundedOverThirtyMinutesAndRemainCancellable(bool exclusive)
    {
        var clock = new Clock();
        var store = new Store();
        var attempts = 0;
        using var cancellation = new CancellationTokenSource();
        Task wait;
        if (exclusive)
        {
            var snapshot = await store.LoadAsync(default);
            snapshot.State.WorkerLease = new("other", Now.AddMinutes(45));
            Assert.True(await store.TrySaveAsync(snapshot, snapshot.State, default));
            var initialReads = store.Reads;
            wait = Worker(clock).RunExclusiveWhenAvailableAsync(store, _ => Task.FromResult(true), cancellation.Token);
            attempts = -initialReads;
        }
        else
        {
            wait = new AutomaticWorkerContinuation(store, clock).RunAsync(_ =>
            {
                attempts++;
                return Task.FromResult(new WorkerResult(WorkerStopReason.LeaseHeld, 0, null));
            }, cancellation.Token);
        }
        while (clock.GetUtcNow() < Now.AddMinutes(30))
        {
            await clock.TimerSignal.WaitAsync(TimeSpan.FromSeconds(10));
            clock.Advance(clock.LastDueTime);
        }
        await clock.TimerSignal.WaitAsync(TimeSpan.FromSeconds(10));
        if (exclusive) attempts += store.Reads;
        Assert.InRange(attempts, 1, 25);
        if (!exclusive) Assert.Equal(0, store.Reads);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task AutomaticContinuationDoesNotResumeSettledOrUncertainWork()
    {
        var options = Fast with { MaximumIterations = 64, TrackAutomaticContinuations = true };
        var settledStore = new Store();
        var settledApi = new OfflineApi(settledStore);
        var settledCalls = 0;
        var settled = await new AutomaticWorkerContinuation(settledStore).RunAsync(token =>
        {
            settledCalls++;
            return Worker().RunAsync(settledStore, settledApi, settledApi, settledApi, Simulation, options, token);
        });
        Assert.Equal(WorkerStopReason.Settled, settled.Reason);
        Assert.Equal(1, settledCalls);

        var uncertainStore = new Store();
        var uncertainApi = new OfflineApi(uncertainStore) { UnknownLaunch = true, RecoveryUnknown = true };
        var uncertainCalls = 0;
        var uncertain = await new AutomaticWorkerContinuation(uncertainStore).RunAsync(token =>
        {
            uncertainCalls++;
            return Worker().RunAsync(uncertainStore, uncertainApi, uncertainApi, uncertainApi, Simulation, options, token);
        });
        Assert.Equal(WorkerStopReason.Blocked, uncertain.Reason);
        Assert.Equal(1, uncertainCalls);
        Assert.Single(uncertainApi.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticContinuationTracksTheSameTaskWhileCiOrReviewIsPending(bool waitForCi)
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteCi = !waitForCi, CompleteReviews = false };
        var options = Fast with { MaximumIterations = 12, TrackAutomaticContinuations = true };
        var result = await new AutomaticWorkerContinuation(store).RunAsync(
            token => Worker().RunAsync(store, api, api, api, Simulation, options, token));

        var issue = store.Current.Issues[7];
        Assert.Equal(WorkerStopReason.ContinuationLimit, result.Reason);
        Assert.Equal(3, issue.Tasks[issue.ActiveTaskId!].AutomaticContinuations);
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Single(api.Executions, kind => kind == EffectKind.RunCi);
        Assert.Equal(waitForCi ? 0 : 1, api.Executions.Count(kind => kind == EffectKind.RequestReview));
    }

    [Fact]
    public async Task RunningRepairKeepsItsOwnContinuationBudgetAcrossWorkerBounds()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CiFailures = 1 };
        api.AfterExecute = () =>
        {
            if (api.LaunchedIssues.Count == 2) api.CompleteTasks = false;
        };
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        var issue = store.Current.Issues[7];
        var repairId = issue.ActiveTaskId!;
        Assert.Equal(Phase.FIX_PENDING, issue.Phase);
        Assert.Equal(TaskKind.CiRepair, issue.Tasks[repairId].Kind);
        Assert.Equal(TaskStatus.Running, issue.Tasks[repairId].Status);

        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        var result = await new AutomaticWorkerContinuation(store).RunAsync(
            token => Worker().RunAsync(store, api, api, api, Simulation, options, token));

        issue = store.Current.Issues[7];
        Assert.Equal(WorkerStopReason.ContinuationLimit, result.Reason);
        Assert.Equal(Phase.BLOCKED, issue.Phase);
        Assert.Equal(repairId, issue.ActiveTaskId);
        Assert.Equal(3, issue.Tasks[repairId].AutomaticContinuations);
        Assert.Equal(2, api.Executions.Count(kind => kind == EffectKind.LaunchTask));
    }

    [Fact]
    public async Task HoldProjectionDoesNotPlanQueuedWorkAndNormalWorkerStartsItAfterRelease()
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], CompleteTasks = false };
        api.AfterExecute = () =>
        {
            if (api.LaunchedIssues.Contains(8)) api.CompleteTasks = false;
        };
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        var projectionOptions = Simulation with { ProjectionOnly = true };
        var projectionCalls = 0;
        async Task<WorkerResult> Publish(CancellationToken token)
        {
            api.CompleteTasks = true;
            var result = await Worker().RunAsync(store, api, api, api, projectionOptions,
                Fast with { MaximumIterations = 8 }, token);
            if (++projectionCalls == 1)
            {
                var state = store.Current;
                Assert.Null(state.AdmittedIssue);
                Assert.Empty(state.Issues[8].Tasks);
                Assert.DoesNotContain(state.Outbox.Values, effect =>
                    effect.IssueNumber == 8 && effect.Kind is EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview);
            }
            return result;
        }

        var result = await new AutomaticWorkerContinuation(store).RunAsync(
            token => Worker().RunAsync(store, api, api, api, Simulation, options, token),
            publishHold: Publish);

        Assert.Equal(WorkerStopReason.ContinuationLimit, result.Reason);
        Assert.True(projectionCalls >= 1);
        Assert.Equal(new[] { 7, 8 }, api.LaunchedIssues);
        Assert.Single(store.Current.Issues[8].Tasks);
    }

    [Fact]
    public async Task ContendedHoldProjectionRetriesBeforeReleasingAdmissionAndStartingQueuedWork()
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], CompleteTasks = false };
        api.AfterExecute = () =>
        {
            if (api.LaunchedIssues.Contains(8)) api.CompleteTasks = false;
        };
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? competing = null;
        var attempts = 0;
        async Task<WorkerResult> Publish(CancellationToken token)
        {
            if (++attempts == 1)
            {
                competing = Worker().RunExclusiveAsync(store, async _ =>
                {
                    acquired.TrySetResult();
                    await release.Task;
                    return true;
                });
                await acquired.Task;
            }
            api.CompleteTasks = true;
            return await Worker().RunAsync(store, api, api, api, Simulation with { ProjectionOnly = true },
                Fast with { MaximumIterations = 8 }, token);
        }

        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        var continuation = new AutomaticWorkerContinuation(store, clock).RunAsync(
            token => Worker().RunAsync(store, api, api, api, Simulation, options, token), publishHold: Publish);
        try
        {
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, attempts);
            Assert.Equal(7, store.Current.AdmittedIssue);
            Assert.Empty(store.Current.Issues[8].Tasks);
            Assert.Equal(new[] { 7 }, api.LaunchedIssues);
        }
        finally
        {
            release.TrySetResult();
            if (competing is not null) await competing;
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(WorkerStopReason.ContinuationLimit, (await continuation).Reason);
        Assert.True(attempts >= 2);
        Assert.Contains(api.Deliveries, delivery => delivery.Issue == 7 &&
            delivery.Kind == EffectKind.ProjectStatus && delivery.StatusBody!.Contains("Agent workflow: **BLOCKED**"));
        Assert.Equal("Automatic continuation limit reached.", store.Current.Issues[7].HoldReason);
        Assert.Equal(new[] { 7, 8 }, api.LaunchedIssues);
        Assert.Single(store.Current.Issues[8].Tasks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HoldProjectionTargetsHeldIssueBeforeUnrelatedBacklogAndPreservesAdmission(
        bool completeTask, bool requiredStatusEnabled)
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], CompleteTasks = false, TrackProjectedLabels = true };
        var worker = new OrchestrationWorker(new OrchestrationReducer(new Authorized(), requiredStatusEnabled));
        var calls = 0;
        var prioritizeHandoff = false;
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        async Task<WorkerResult> Publish(int issueNumber, CancellationToken token)
        {
            if (++calls == 1)
            {
                Assert.Equal(7, issueNumber);
                var snapshot = await store.LoadAsync(token);
                foreach (var number in Enumerable.Range(100, 10))
                    snapshot.State.Issues.Add(number, new IssueState
                    {
                        Number = number, Phase = Phase.BLOCKED, ReactivationRequired = true, HoldReason = "Existing hold."
                    });
                api.AdditionalObservedIssues = Enumerable.Range(100, 10).ToArray();
                var observation = await api.ObserveAsync(snapshot.State, token);
                observation.Issues.Remove(7);
                observation.Issues.Remove(8);
                var planned = new OrchestrationReducer(new Authorized(), requiredStatusEnabled)
                    .Reconcile(snapshot.State, observation, projectionOnly: true).State;
                Assert.True(await store.TrySaveAsync(snapshot, planned, token));
                Assert.True(store.Current.Outbox.Values.Count(effect => effect.IssueNumber >= 100 &&
                    effect.Status == DeliveryStatus.Pending) > 8);
                api.CompleteTasks = completeTask;
            }
            var before = api.Executions.Count;
            var result = await worker.RunAsync(store, api, api, api,
                Simulation with { ProjectionOnly = true, ProjectionIssueNumber = issueNumber },
                Fast with { MaximumIterations = 8 }, token);
            GitHubOrchestrationHost.VerifyHoldProjection(result, issueNumber, requiredStatusEnabled);
            prioritizeHandoff = true;
            Assert.All(api.Deliveries.Skip(before), delivery =>
            {
                Assert.Equal(issueNumber, delivery.Issue);
                Assert.Contains(delivery.Kind, new[] { EffectKind.ProjectLabels, EffectKind.ProjectStatus, EffectKind.PublishRequiredStatus });
            });
            if (calls == 1)
            {
                Assert.Empty(store.Current.Issues[8].Tasks);
                Assert.Equal(completeTask ? null : 7, store.Current.AdmittedIssue);
                Assert.Equal(!completeTask, store.Current.Issues[7].ContinuationHoldBarrier is not null);
                Assert.Contains("agent:blocked", api.ProjectedLabels[7]);
                Assert.Contains(api.Deliveries, delivery => delivery.Issue == 7 &&
                    delivery.Kind == EffectKind.ProjectStatus && delivery.StatusBody!.Contains("Agent workflow: **BLOCKED**"));
                if (requiredStatusEnabled && store.Current.Issues[7].PullRequest is not null)
                    Assert.Contains(store.Current.Outbox.Values, effect => effect.IssueNumber == 7 &&
                        effect.Kind == EffectKind.PublishRequiredStatus && effect.Status == DeliveryStatus.Succeeded &&
                        effect.RequiredStatus?.State == "error");
            }
            return result;
        }

        var result = await new AutomaticWorkerContinuation(store).RunForIssueAsync(
            token => worker.RunAsync(store, api, api, api,
                Simulation with { PrioritizeAdmittedWork = prioritizeHandoff }, options, token), Publish);

        Assert.Equal(WorkerStopReason.ContinuationLimit, result.Reason);
        Assert.Equal(completeTask ? new[] { 7, 8 } : new[] { 7 }, api.LaunchedIssues);
        Assert.Equal(completeTask ? 1 : 0, store.Current.Issues[8].Tasks.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedHoldProjectionFailsWithoutStartingQueuedWork(bool uncertain)
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], CompleteTasks = false, TrackProjectedLabels = true };
        var worker = Worker();
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        async Task<WorkerResult> Publish(int issueNumber, CancellationToken token)
        {
            if (uncertain) api.UnknownProjection = EffectKind.ProjectStatus;
            var projection = await worker.RunAsync(store, api, api, api,
                Simulation with { ProjectionOnly = true, ProjectionIssueNumber = issueNumber },
                Fast with { MaximumIterations = uncertain ? 8 : 1 }, token);
            GitHubOrchestrationHost.VerifyHoldProjection(projection, issueNumber, false);
            return projection;
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutomaticWorkerContinuation(store).RunForIssueAsync(
                token => worker.RunAsync(store, api, api, api, Simulation, options, token), Publish));
        Assert.Contains("not confirmed", error.Message);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Empty(store.Current.Issues[8].Tasks);
        Assert.Equal(new[] { 7 }, api.LaunchedIssues);
        if (uncertain)
            Assert.Single(store.Current.Outbox.Values, effect => effect.IssueNumber == 7 &&
                effect.Kind == EffectKind.ProjectStatus && effect.Status == DeliveryStatus.Unknown);
    }

    [Fact]
    public async Task UncertainRequiredStatusDoesNotAuthorizeQueueHandoff()
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8], CompleteTasks = false, RecoveryUnknown = true };
        var worker = new OrchestrationWorker(new OrchestrationReducer(new Authorized(), requiredStatusEnabled: true));
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        async Task<WorkerResult> Publish(int issueNumber, CancellationToken token)
        {
            api.CompleteTasks = true;
            api.UnknownProjection = EffectKind.PublishRequiredStatus;
            var projection = await worker.RunAsync(store, api, api, api,
                Simulation with { ProjectionOnly = true, ProjectionIssueNumber = issueNumber },
                Fast with { MaximumIterations = 8 }, token);
            GitHubOrchestrationHost.VerifyHoldProjection(projection, issueNumber, true);
            return projection;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutomaticWorkerContinuation(store).RunForIssueAsync(
                token => worker.RunAsync(store, api, api, api, Simulation, options, token), Publish));
        Assert.Single(store.Current.Outbox.Values, effect => effect.IssueNumber == 7 &&
            effect.Kind == EffectKind.PublishRequiredStatus && effect.Status == DeliveryStatus.Unknown);
        Assert.Equal(TaskStatus.Completed, store.Current.Issues[7].Tasks[store.Current.Issues[7].ActiveTaskId!].Status);
        Assert.True(store.Current.Issues[7].ContinuationHoldBarrier!.RequireRequiredStatus);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Empty(store.Current.Issues[8].Tasks);
        Assert.Equal(new[] { 7 }, api.LaunchedIssues);

        await worker.RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.NotNull(store.Current.Issues[7].ContinuationHoldBarrier);
        Assert.Empty(store.Current.Issues[8].Tasks);
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.NotNull(store.Current.Issues[7].ContinuationHoldBarrier);
        Assert.Empty(store.Current.Issues[8].Tasks);
        var noAdmission = StateJson.Clone(store.Current);
        noAdmission.AdmittedIssue = null;
        var decision = new OrchestrationReducer(new Authorized(), requiredStatusEnabled: true)
            .Reconcile(noAdmission, await api.ObserveAsync(noAdmission, CancellationToken.None));
        Assert.Null(decision.State.AdmittedIssue);
        Assert.Empty(decision.State.Issues[8].Tasks);
        decision.State.Outbox["blocked-launch"] = new EffectIntent
        {
            Id = "blocked-launch", IssueNumber = 8, Kind = EffectKind.LaunchTask, Status = DeliveryStatus.Pending
        };
        Assert.False(OrchestrationContinuation.HasWork(decision.State,
            Simulation with { AllowedEffectKinds = new HashSet<EffectKind> { EffectKind.LaunchTask } }));
        Assert.Throws<InvalidOperationException>(() =>
            new OrchestrationReducer().MarkExecuting(decision.State, "blocked-launch", Now));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new OrchestrationReconciler(new OrchestrationReducer(new Authorized())).DispatchAsync(store,
                new("reactivate-held", "owner/repo", 7, 1, 1, new("maintainer", ActorKind.Human), Now,
                    new Reactivate("Reactivation must wait for publication."))));

        api.RecoveryUnknown = false;
        api.UnknownProjection = null;
        await worker.RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Null(store.Current.Issues[7].ContinuationHoldBarrier);
        Assert.Contains(store.Current.Outbox.Values, effect => effect.IssueNumber == 7 &&
            effect.Kind == EffectKind.PublishRequiredStatus && effect.Status == DeliveryStatus.Succeeded &&
            effect.RequiredStatus?.State == "error");
        Assert.Contains(8, api.LaunchedIssues);
    }

    [Fact]
    public async Task PublishedRunningHoldRemainsBarredUntilItsFinalHeadStatusIsConfirmed()
    {
        var store = new Store();
        var api = new OfflineApi(store)
        {
            ReadyIssues = [7, 8], NoInitialPullRequest = true, CompleteTasks = false, RecoveryUnknown = true
        };
        var worker = new OrchestrationWorker(new OrchestrationReducer(new Authorized(), requiredStatusEnabled: true));
        var options = Fast with { MaximumIterations = 3, TrackAutomaticContinuations = true };
        async Task<WorkerResult> Publish(int issueNumber, CancellationToken token)
        {
            var projection = await worker.RunAsync(store, api, api, api,
                Simulation with { ProjectionOnly = true, ProjectionIssueNumber = issueNumber },
                Fast with { MaximumIterations = 8 }, token);
            GitHubOrchestrationHost.VerifyHoldProjection(projection, issueNumber, true);
            return projection;
        }

        await new AutomaticWorkerContinuation(store).RunForIssueAsync(
            token => worker.RunAsync(store, api, api, api, Simulation, options, token), Publish);
        Assert.Null(store.Current.Issues[7].PullRequest);
        Assert.NotNull(store.Current.Issues[7].ContinuationHoldBarrier);
        Assert.Equal(7, store.Current.AdmittedIssue);

        api.CompleteTasks = true;
        api.UnknownProjection = EffectKind.PublishRequiredStatus;
        await worker.RunAsync(store, api, api, api, Simulation, Fast);
        Assert.NotNull(store.Current.Issues[7].PullRequest);
        Assert.NotNull(store.Current.Issues[7].ContinuationHoldBarrier);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Empty(store.Current.Issues[8].Tasks);
        Assert.Single(store.Current.Outbox.Values, effect => effect.IssueNumber == 7 &&
            effect.Kind == EffectKind.PublishRequiredStatus && effect.Status == DeliveryStatus.Unknown);
    }

    [Fact]
    public async Task HoldProjectionLeaseTimeoutFailsWithoutInspectingOrStartingQueue()
    {
        var clock = new Clock();
        var store = new Store();
        var runs = 0;
        var projections = 0;
        var continuation = new AutomaticWorkerContinuation(store, clock).RunAsync(_ =>
        {
            runs++;
            return Task.FromResult(new WorkerResult(WorkerStopReason.ContinuationLimit, 0, null) { LeaseAdmittedIssue = 7 });
        }, publishHold: _ =>
        {
            projections++;
            return Task.FromResult(new WorkerResult(WorkerStopReason.LeaseHeld, 0, null));
        });
        await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(10));
        clock.Advance(LeaseWait.Limit);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => continuation);
        Assert.Contains("hold publication needs operator attention", error.Message);
        Assert.Equal(1, runs);
        Assert.Equal(2, projections);
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task ProjectionDefersUnheldTaskCompletionUntilNormalPlanning()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        await Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumIterations = 3 });
        api.CompleteTasks = true;

        await Worker().RunAsync(store, api, api, api, Simulation with { ProjectionOnly = true }, Fast);

        var issue = store.Current.Issues[7];
        Assert.Equal(TaskStatus.AwaitingHeadConfirmation, issue.Tasks[issue.ActiveTaskId!].Status);
        Assert.Null(issue.Ci);
        Assert.DoesNotContain(store.Current.Outbox.Values, effect => effect.Kind is EffectKind.RunCi or EffectKind.RequestReview);
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Single(api.Executions, kind => kind == EffectKind.RunCi);
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task ProjectionDoesNotRetryAnUncertainLaunch()
    {
        var store = new Store();
        var api = new OfflineApi(store) { UnknownLaunch = true, RecoveryUnknown = true };
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        await Worker().RunAsync(store, api, api, api, Simulation with { ProjectionOnly = true }, Fast);

        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Equal(Phase.BLOCKED, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task ExclusiveRecoveryCannotRunWhileAnotherWorkerOwnsLease()
    {
        var store = new Store();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new OfflineApi(store)
        {
            CompleteTasks = false,
            BeforeExternalResult = () => { started.TrySetResult(); return finish.Task; }
        };
        var owner = Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumIterations = 2 });
        await started.Task;
        var recoveryStarted = false;
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Worker().RunExclusiveAsync(store, _ => { recoveryStarted = true; return Task.FromResult(true); }));
            Assert.False(recoveryStarted);
        }
        finally
        {
            finish.TrySetResult();
            await owner;
        }
    }

    [Fact]
    public async Task ConcurrentWorkersPublishExactlyOneLaunchBeforeCallingAdapter()
    {
        var store = new Store();
        var api = new OfflineApi(store) { CompleteTasks = false };
        var bounded = Fast with { MaximumIterations = 3 };
        await Task.WhenAll(
            Worker().RunAsync(store, api, api, api, Simulation, bounded),
            Worker().RunAsync(store, api, api, api, Simulation, bounded));

        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Single(store.Current.Issues[7].Tasks);
        Assert.Equal(7, store.Current.AdmittedIssue);
    }

    [Fact]
    public async Task ConcurrentResumeCannotRecoverAnOperationItsOwnerIsStillExecuting()
    {
        var store = new Store();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new OfflineApi(store)
        {
            CompleteTasks = false,
            BeforeExternalResult = () => { started.TrySetResult(); return finish.Task; }
        };
        var owner = Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumIterations = 2 });
        await started.Task;
        var concurrent = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.LeaseHeld, concurrent.Reason);
        Assert.Equal(0, api.Recoveries);
        Assert.Equal(Phase.CODING, store.Current.Issues[7].Phase);
        finish.SetResult();
        await owner;
        Assert.Null(store.Current.WorkerLease);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(Phase.CODING, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task CorruptLeaseFailsClosedWithoutOverwritingIt()
    {
        var store = new Store();
        var snapshot = await store.LoadAsync(CancellationToken.None);
        snapshot.State.WorkerLease = new("", default);
        Assert.True(await store.TrySaveAsync(snapshot, snapshot.State, CancellationToken.None));
        var before = StateJson.Serialize(store.Current);
        var api = new OfflineApi(store);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast));
        Assert.Equal(before, StateJson.Serialize(store.Current));
        Assert.Empty(api.Executions);
        Assert.Equal(0, api.Recoveries);
    }

    [Fact]
    public async Task CancellationAfterExternalCallLeavesExecutingIntentForLookupOnlyResume()
    {
        var store = new Store();
        using var cancellation = new CancellationTokenSource();
        var api = new OfflineApi(store) { AfterExecute = cancellation.Cancel };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast, cancellation.Token));
        Assert.Single(store.Current.Outbox.Values,
            effect => effect.Kind == EffectKind.LaunchTask && effect.Status == DeliveryStatus.Executing);
        api.AfterExecute = null;
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(1, api.Recoveries);
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task FailedResultPublicationIsRecoveredWithoutRepeatingExternalCall()
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { AfterExecute = () => store.FailWrites = true };
        await Assert.ThrowsAsync<IOException>(() =>
            Worker(clock).RunAsync(store, api, api, api, Simulation, Fast));
        Assert.Single(api.Executions);
        Assert.Single(store.Current.Outbox.Values,
            effect => effect.Kind == EffectKind.LaunchTask && effect.Status == DeliveryStatus.Executing);

        store.FailWrites = false;
        api.AfterExecute = null;
        Assert.Equal(WorkerStopReason.LeaseHeld,
            (await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast)).Reason);
        clock.Advance(TimeSpan.FromMinutes(31));
        await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(1, api.Recoveries);
    }

    [Fact]
    public async Task UnknownOutcomeStopsTheWorkerAndResumeDoesNotClearItsHold()
    {
        var store = new Store();
        var api = new OfflineApi(store) { UnknownLaunch = true, RecoveryUnknown = true, IncludeUnpublishedResults = true };
        var first = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.Blocked, first.Reason);
        var held = store.Current.Issues[7];
        var resumed = await Worker().RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Equal(WorkerStopReason.Blocked, resumed.Reason);
        Assert.Equal(held.HoldReason, store.Current.Issues[7].HoldReason);
        Assert.Equal(held.ActiveTaskId, store.Current.Issues[7].ActiveTaskId);
        Assert.Equal(held.ConsecutiveCiFailures, store.Current.Issues[7].ConsecutiveCiFailures);
        Assert.Equal(held.Round, store.Current.Issues[7].Round);
        Assert.Equal(Phase.BLOCKED, store.Current.Issues[7].Phase);
        Assert.Equal(7, store.Current.AdmittedIssue);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(1, api.Recoveries);
        Assert.DoesNotContain(store.Current.Inbox.Values, entry => entry.Event?.Data is TaskCompleted);
    }

    [Theory]
    [InlineData(EffectKind.LaunchTask)]
    [InlineData(EffectKind.RunCi)]
    [InlineData(EffectKind.RequestReview)]
    public async Task CompletionArrivingBeforeOutcomePublicationDoesNotPreventIdentityRecovery(EffectKind interrupted)
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { IncludeUnpublishedResults = true };
        api.AfterExecute = () =>
        {
            if (api.Executions.Last() == interrupted) store.FailWrites = true;
        };
        await Assert.ThrowsAsync<IOException>(() =>
            Worker(clock).RunAsync(store, api, api, api, Simulation, Fast));
        Assert.Single(store.Current.Outbox.Values,
            effect => effect.Kind == interrupted && effect.Status == DeliveryStatus.Executing);
        store.FailWrites = false;
        api.AfterExecute = null;
        clock.Advance(TimeSpan.FromMinutes(31));
        await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
        Assert.Equal(1, api.Recoveries);
        Assert.Single(api.Executions, kind => kind == interrupted);
    }

    [Fact]
    public async Task DryRunAppliesTheSameEventsAndDecisionsWithoutPersistingOrExecuting()
    {
        var store = new Store();
        var api = new OfflineApi(store);
        var result = await Worker().RunAsync(store, api, api, api, options: Fast);

        Assert.Equal(WorkerStopReason.DryRun, result.Reason);
        Assert.Equal(Phase.CODING, result.LastReconciliation!.State.Issues[7].Phase);
        Assert.Contains(result.LastReconciliation.Decisions, effect => effect.Kind == EffectKind.LaunchTask);
        Assert.Empty(store.Current.Issues);
        Assert.Equal(0, store.Writes);
        Assert.Empty(api.Executions);
    }

    [Fact]
    public async Task DryRunDefersUnconfirmedCompletionWithoutRecoveringOrAcknowledging()
    {
        var store = new Store();
        using var cancellation = new CancellationTokenSource();
        var api = new OfflineApi(store) { AfterExecute = cancellation.Cancel, IncludeUnpublishedResults = true };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast, cancellation.Token));
        var writes = store.Writes;
        var result = await Worker().RunAsync(store, api, api, api, options: Fast);
        Assert.Equal(WorkerStopReason.EventBacklog, result.Reason);
        Assert.Equal(writes, store.Writes);
        Assert.Equal(0, api.Recoveries);
        Assert.DoesNotContain(result.LastReconciliation!.State.Inbox.Values, entry => entry.Event?.Data is TaskCompleted);
    }

    [Fact]
    public async Task DisabledEffectsAcceptEventsButDoNotWaitOrExecute()
    {
        var store = new Store();
        var api = new OfflineApi(store);
        var result = await Worker().RunAsync(store, api, api, api, new(DryRun: false), Fast);

        Assert.Equal(WorkerStopReason.EffectsDisabled, result.Reason);
        Assert.Equal(1, result.Iterations);
        Assert.Contains("ready", store.Current.Inbox.Keys);
        Assert.Empty(api.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedPassStopsAfterSuccessOrCiFailureAndReplayDoesNotPoll(bool ciFailure)
    {
        var store = new Store();
        var api = new OfflineApi(store)
        {
            SinglePassReady = true, ConfirmCiDispatch = true, CiFailures = ciFailure ? 1 : 0
        };
        var allowed = new HashSet<EffectKind>
        {
            EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.PublishRequiredStatus
        };
        var reconcile = new ReconcileOptions(false, true, true, true,
            AllowOfflineAgentSimulation: true, AllowedEffectKinds: allowed,
            AllowedRequiredStatusStates: new HashSet<string>(["success"], StringComparer.Ordinal),
            MaximumEffectExecutions: 3);
        var worker = new OrchestrationWorker(
            new(new Authorized(), requiredStatusEnabled: true, reviewRequestsEnabled: false));

        var result = await worker.RunAsync(store, api, api, api, reconcile, Fast);

        Assert.Equal(ciFailure ? WorkerStopReason.Blocked : WorkerStopReason.EffectLimit, result.Reason);
        Assert.True(result.Iterations < Fast.MaximumIterations);
        Assert.Equal(ciFailure ? new[] { EffectKind.LaunchTask, EffectKind.RunCi } : new[]
        {
            EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.PublishRequiredStatus
        }, api.Executions);
        Assert.Single(store.Current.Issues[7].Tasks);
        Assert.Equal(ciFailure ? Phase.BLOCKED : Phase.REVIEW_PENDING, store.Current.Issues[7].Phase);
        Assert.Null(store.Current.Issues[7].Review);
        if (!ciFailure)
            Assert.Contains(store.Current.Outbox.Values, effect =>
                effect.Kind == EffectKind.PublishRequiredStatus &&
                effect.RequiredStatus?.State == "success" && effect.Status == DeliveryStatus.Succeeded);
        Assert.Contains(store.Current.Outbox.Values, effect =>
            effect.Status == DeliveryStatus.Pending && !reconcile.AllowsEffect(effect));
        Assert.False(store.Current.Continuation!.Pending);

        var executionCount = api.Executions.Count;
        var excluded = store.Current.Outbox.Values.Where(effect =>
            effect.Status == DeliveryStatus.Pending && !reconcile.AllowsEffect(effect)).Select(effect => effect.Id).ToArray();
        var replay = await worker.RunAsync(store, api, api, api, reconcile, Fast);
        Assert.Equal(ciFailure ? WorkerStopReason.Blocked : WorkerStopReason.Settled, replay.Reason);
        Assert.Equal(1, replay.Iterations);
        Assert.Equal(executionCount, api.Executions.Count);
        Assert.DoesNotContain(EffectKind.RequestReview, api.Executions);
        Assert.All(excluded, id => Assert.Equal(DeliveryStatus.Pending, store.Current.Outbox[id].Status));
        Assert.False(store.Current.Continuation!.Pending);
    }

    [Fact]
    public async Task IsolatedPassStillWaitsForRunningTaskAndCi()
    {
        var store = new Store();
        var api = new OfflineApi(store) { SinglePassReady = true, CompleteTasks = false };
        var reconcile = Simulation with
        {
            AllowedEffectKinds = new HashSet<EffectKind>
                { EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.PublishRequiredStatus },
            AllowedRequiredStatusStates = new HashSet<string>(["success"], StringComparer.Ordinal),
            MaximumEffectExecutions = 3
        };
        var worker = new OrchestrationWorker(new(new Authorized(), true, reviewRequestsEnabled: false));
        var bounded = Fast with { MaximumIterations = 3 };
        var coding = await worker.RunAsync(store, api, api, api, reconcile, bounded);
        Assert.Equal(WorkerStopReason.IterationLimit, coding.Reason);
        Assert.Equal(Phase.CODING, store.Current.Issues[7].Phase);
        Assert.True(store.Current.Continuation!.Pending);

        api.CompleteTasks = true;
        api.CompleteCi = false;
        var ci = await worker.RunAsync(store, api, api, api, reconcile, bounded);
        Assert.Equal(WorkerStopReason.IterationLimit, ci.Reason);
        Assert.Equal(Phase.CI_PENDING, store.Current.Issues[7].Phase);
        Assert.NotNull(store.Current.Issues[7].Ci!.RunId);
        Assert.True(store.Current.Continuation!.Pending);
        Assert.Equal(new[] { EffectKind.LaunchTask, EffectKind.RunCi }, api.Executions);
    }

    [Fact]
    public async Task ObservationFailureAfterEventCommitLeavesTheCommandRecoverable()
    {
        var store = new Store();
        var api = new OfflineApi(store) { FailObservation = true };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast));
        Assert.Contains("ready", store.Current.Inbox.Keys);
        Assert.Empty(api.Executions);

        api.FailObservation = false;
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
    }

    [Fact]
    public async Task DurationLimitYieldsAfterCommittedEventsBeforeStartingAnEffect()
    {
        var clock = new Clock();
        var store = new Store();
        store.AfterSave = () =>
        {
            if (store.Current.Inbox.ContainsKey("ready")) clock.Advance(TimeSpan.FromMinutes(30));
        };
        var api = new OfflineApi(store);
        var result = await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);

        Assert.Equal(WorkerStopReason.TimeLimit, result.Reason);
        Assert.Contains("ready", store.Current.Inbox.Keys);
        Assert.Empty(api.Executions);
        store.AfterSave = null;
        await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
    }

    [Fact]
    public async Task PreCancelledInvocationDoesNotReadWriteOrExecute()
    {
        var store = new Store();
        var api = new OfflineApi(store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast, cancellation.Token));
        Assert.Equal(0, store.Reads);
        Assert.Empty(store.Current.Inbox);
        Assert.Null(store.Current.WorkerLease);
        Assert.Empty(api.Executions);
    }

    [Fact]
    public async Task OversizedBatchIsRejectedBeforeAnyEventCommit()
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8] };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, Fast with { MaximumEventsPerIteration = 1 }));
        Assert.Empty(store.Current.Inbox);
        Assert.Null(store.Current.WorkerLease);
        Assert.Empty(api.Executions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingEventPagesAreDrainedBeforeAnyNewEffectEvenAcrossYield(bool yield)
    {
        var store = new Store();
        var api = new OfflineApi(store);
        api.Backlog.Enqueue(new([new("ready", "owner/repo", 7, 1, 1,
            new("maintainer", ActorKind.Human), Now, new Ready())], HasMore: true));
        api.Backlog.Enqueue(new([new("pause", "owner/repo", 7, 1, 1,
            new("maintainer", ActorKind.Human), Now, new Pause("Stop before launch."))]));
        var first = await Worker().RunAsync(store, api, api, api, Simulation,
            yield ? Fast with { MaximumIterations = 1 } : Fast);
        if (yield)
        {
            Assert.Equal(WorkerStopReason.IterationLimit, first.Reason);
            Assert.Equal(Phase.READY, store.Current.Issues[7].Phase);
            await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        }
        Assert.Equal(Phase.BLOCKED, store.Current.Issues[7].Phase);
        Assert.Equal("Stop before launch.", store.Current.Issues[7].HoldReason);
        Assert.Empty(store.Current.Issues[7].Tasks);
        Assert.DoesNotContain(EffectKind.LaunchTask, api.Executions);
    }

    [Fact]
    public async Task DryRunDoesNotProposeEffectsWhileInputIsIncomplete()
    {
        var store = new Store();
        var api = new OfflineApi(store);
        api.Backlog.Enqueue(new([new("ready", "owner/repo", 7, 1, 1,
            new("maintainer", ActorKind.Human), Now, new Ready())], HasMore: true));
        var result = await Worker().RunAsync(store, api, api, api, options: Fast);
        Assert.Equal(WorkerStopReason.EventBacklog, result.Reason);
        Assert.Empty(result.LastReconciliation!.Decisions);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task MultipleReadyIssuesAreQueuedAndRunSeriallyWithoutOperatorAdvancement()
    {
        var store = new Store();
        var api = new OfflineApi(store) { ReadyIssues = [7, 8] };
        var result = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.Settled, result.Reason);
        Assert.All(store.Current.Issues.Values, issue => Assert.Equal(Phase.HUMAN_REVIEW, issue.Phase));
        Assert.Equal(new[] { 7, 8 }, api.LaunchedIssues);
        Assert.Null(store.Current.AdmittedIssue);
    }

    [Fact]
    public async Task AmbiguousReviewWaitsForClassificationThenResumeAutomaticallyAssemblesRepair()
    {
        var store = new Store();
        var api = new OfflineApi(store) { AmbiguousFirstReview = true };
        var first = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.Blocked, first.Reason);
        Assert.Equal("Review requires human classification.", store.Current.Issues[7].HoldReason);
        var handoff = store.Current.Issues[7].CurrentHandoffId;
        var before = api.Executions.Count;
        await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(before, api.Executions.Count);
        Assert.Equal(handoff, store.Current.Issues[7].CurrentHandoffId);

        api.ClassifyBlocking = true;
        var resumed = await Worker().RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.Settled, resumed.Reason);
        var repair = Assert.Single(store.Current.Issues[7].Tasks.Values, task => task.Kind == TaskKind.ReviewRepair);
        Assert.Equal("finding", Assert.Single(repair.ReviewBlockers).Id);
        Assert.Single(store.Current.Issues[7].Classifications);
        Assert.Equal(2, api.Executions.Count(kind => kind == EffectKind.LaunchTask));
        Assert.Equal(2, api.Executions.Count(kind => kind == EffectKind.RunCi));
        Assert.Equal(2, api.Executions.Count(kind => kind == EffectKind.RequestReview));
    }

    [Fact]
    public async Task DeadlineDuringExternalCallYieldsWithExecutingIntentAndResumeOnlyReconciles()
    {
        var clock = new Clock();
        var store = new Store();
        var api = new OfflineApi(store) { AfterExecute = () => clock.Advance(TimeSpan.FromMinutes(30)) };
        var result = await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Equal(WorkerStopReason.TimeLimit, result.Reason);
        Assert.Single(store.Current.Outbox.Values,
            effect => effect.Kind == EffectKind.LaunchTask && effect.Status == DeliveryStatus.Executing);

        api.AfterExecute = null;
        await Worker(clock).RunAsync(store, api, api, api, Simulation, Fast);
        Assert.Single(api.Executions, kind => kind == EffectKind.LaunchTask);
        Assert.Equal(1, api.Recoveries);
        Assert.Equal(Phase.HUMAN_REVIEW, store.Current.Issues[7].Phase);
    }

    [Theory]
    [InlineData(0, 1, 1, 0)]
    [InlineData(1, 0, 1, 0)]
    [InlineData(1, 2049, 1, 0)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(1, 1, 31, 0)]
    [InlineData(1, 1, 1, -1)]
    [InlineData(1, 1, 1, 2)]
    public async Task InvalidBoundsAreRejectedBeforeReadingState(int iterations, int events, int duration, int poll)
    {
        var store = new Store();
        var api = new OfflineApi(store);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Worker().RunAsync(store, api, api, api, Simulation, new()
            {
                MaximumIterations = iterations, MaximumEventsPerIteration = events,
                MaximumDuration = TimeSpan.FromMinutes(duration), PollInterval = TimeSpan.FromMinutes(poll)
            }));
        Assert.Equal(0, store.Reads);
    }

    private static OrchestrationWorker Worker(TimeProvider? timeProvider = null) =>
        new(new OrchestrationReducer(new Authorized()), timeProvider);

    private sealed class Authorized : IAuthorizationPolicy
    {
        public bool IsAuthorized(OrchestrationEvent message) => true;
    }

    private sealed class BlockingSource : IWorkerEventSource
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new([]);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim TimerSignal { get; } = new(0);
        public TimeSpan LastDueTime { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            LastDueTime = dueTime;
            TimerSignal.Release();
            TimerCreated.TrySetResult();
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            ticks += duration.Ticks;
            foreach (var timer in timers.ToList()) timer.Fire();
        }

        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            private long? due;
            private TimeSpan period;
            public bool Change(TimeSpan dueTime, TimeSpan repeat)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.ticks + dueTime.Ticks;
                period = repeat;
                return true;
            }
            public void Fire()
            {
                if (due is null || due > clock.ticks) return;
                due = period == Timeout.InfiniteTimeSpan ? null : clock.ticks + period.Ticks;
                callback(state);
            }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Store : IStateStore
    {
        private readonly object sync = new();
        private string json = StateJson.Serialize(RepositoryState.Create("owner/repo"));
        private int version;
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public bool FailWrites { get; set; }
        public Action? AfterSave { get; set; }
        public RepositoryState Current { get { lock (sync) return StateJson.Deserialize(json); } }
        public async Task<StateSnapshot> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            lock (sync)
            {
                Reads++;
                return new(version.ToString(), StateJson.Deserialize(json));
            }
        }
        public async Task<bool> TrySaveAsync(StateSnapshot expected, RepositoryState state, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            lock (sync)
            {
                if (FailWrites) throw new IOException("Injected publication failure.");
                if (expected.Revision != version.ToString()) return false;
                json = StateJson.Serialize(state);
                version++;
                Writes++;
                AfterSave?.Invoke();
                return true;
            }
        }
    }

    private sealed class OfflineApi(Store store) : IWorkerEventSource, IObservationSource, IOfflineEffectAdapter
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EffectOutcome> outcomes = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, string> heads = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, OrchestrationEvent> messages = new();
        public List<EffectKind> Executions { get; } = [];
        public List<(int Issue, EffectKind Kind, int? Admission, string? StatusBody)> Deliveries { get; } = [];
        public Dictionary<int, List<string>> ProjectedLabels { get; } = [];
        public List<int> LaunchedIssues { get; } = [];
        public int Recoveries { get; private set; }
        public int CiFailures { get; init; }
        public int ReviewBlockers { get; init; }
        public int[] ReadyIssues { get; init; } = [7];
        public bool CompleteTasks { get; set; } = true;
        public bool CompleteCi { get; set; } = true;
        public bool CompleteReviews { get; set; } = true;
        public bool SinglePassReady { get; init; }
        public bool ConfirmCiDispatch { get; init; }
        public bool NoInitialPullRequest { get; init; }
        public string? CompletionMismatch { get; set; }
        public bool UnknownLaunch { get; init; }
        public bool RecoveryUnknown { get; set; }
        public bool IncludeUnpublishedResults { get; init; }
        public bool FailObservation { get; set; }
        public bool AmbiguousFirstReview { get; init; }
        public bool TrackProjectedLabels { get; init; }
        public int[] AdditionalObservedIssues { get; set; } = [];
        public EffectKind? UnknownProjection { get; set; }
        public bool ClassifyBlocking { get; set; }
        public Queue<WorkerEventBatch> Backlog { get; } = [];
        public Action? AfterExecute { get; set; }
        public Func<Task>? BeforeExternalResult { get; init; }

        public Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken cancellationToken)
        {
            if (Backlog.Count > 0) return Task.FromResult(Backlog.Dequeue());
            var result = new List<OrchestrationEvent>();
            foreach (var number in ReadyIssues)
            {
                Add(new Ready(SinglePassTest: SinglePassReady), number == 7 ? "ready" : $"ready:{number}", number);
                if (!state.Issues.TryGetValue(number, out var issue)) continue;
                if (CompleteTasks && issue.ActiveTaskId is { } id &&
                    (issue.Tasks[id].Status == TaskStatus.Running || IncludeUnpublishedResults &&
                        issue.Tasks[id].Status is TaskStatus.Executing or TaskStatus.Unknown &&
                        outcomes.ContainsKey(state.Outbox.Values.Single(effect =>
                            effect.Kind == EffectKind.LaunchTask && effect.TaskId == id).Id)))
                {
                    var task = issue.Tasks[id];
                    var launch = state.Outbox.Values.Single(effect => effect.Kind == EffectKind.LaunchTask && effect.TaskId == id);
                    var evidence = new CandidateShaEvidence(TaskTerminalRecovery.CandidateShaEvidenceVersion,
                        TaskTerminalRecovery.CandidateShaPolicy, id, task.ExternalId ?? $"external:{launch.Id}",
                        [$"session:{id}"], number + 10, Head(number));
                    Add(new TaskCompleted(id, number + 10, Head(number), evidence), $"completed:{id}", number);
                }
                if (CompleteCi && issue.Ci is { Green: null } ci &&
                    (ci.RunId ?? UnpublishedId(ci.IntentId)) is { } runId)
                {
                    var green = issue.Tasks.Values.Count(task => task.Kind == TaskKind.CiRepair) >= CiFailures;
                    Add(new CiResult(runId, ci.TaskId, ci.Round, number + 10, ci.Sha, green,
                        green ? [] : [new("ci:compile", "Compiler failure", "Build job compiler diagnostic")]),
                        $"ci:{runId}", number);
                }
                if (CompleteReviews && issue.Review is { Classification: null } review &&
                    (review.ReviewId ?? UnpublishedId(review.IntentId)) is { } reviewId)
                {
                    var blocked = issue.Tasks.Values.Count(task => task.Kind == TaskKind.ReviewRepair) < ReviewBlockers;
                    var ambiguous = AmbiguousFirstReview && !issue.Tasks.Values.Any(task => task.Kind == TaskKind.ReviewRepair);
                    Add(new ReviewResult(reviewId, review.TaskId, review.Round, number + 10, review.Sha,
                        ambiguous ? ReviewClassification.NeedsHuman :
                        blocked ? ReviewClassification.Blocking : ReviewClassification.Accepted,
                        blocked ? [new("finding", "Repair the defect", "file.cs: missing validation")] : [],
                        blocked ? [] : review.Findings.Select(finding =>
                            new FindingResponse(finding.Id, "file.cs: validation added and regression reviewed", true)).ToList()),
                        $"review:{reviewId}", number);
                }
                if (ClassifyBlocking && issue.Phase == Phase.BLOCKED && issue.Review is { } classifiedReview &&
                    issue.CurrentHandoffId is { } handoff)
                    Add(new HumanClassification(handoff, issue.Round, number + 10, classifiedReview.Sha,
                        ReviewClassification.Blocking, "Confirmed defect needing validation",
                        [new("finding", "Repair the defect", "file.cs: missing validation")]),
                        $"classification:{handoff}", number);
            }
            return Task.FromResult(new WorkerEventBatch(result));

            void Add(EventData data, string id, int number)
            {
                // Repeated API observations return the original immutable event, including its timestamp.
                var message = messages.GetOrAdd(id, _ =>
                {
                    var kind = data switch
                    {
                        TaskCompleted => ActorKind.TaskService,
                        CiResult => ActorKind.CiService,
                        ReviewResult => ActorKind.ReviewService,
                        _ => ActorKind.Human
                    };
                    return new(id, "owner/repo", number, 1, 1,
                        new Actor("fixture-service", kind), Now, data);
                });
                result.Add(message);
            }
        }

        public Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken)
        {
            if (FailObservation) throw new HttpRequestException("Injected observation failure.");
            return Task.FromResult(new LiveObservation
            {
                ObservedAt = Now,
                Issues = ReadyIssues.Concat(AdditionalObservedIssues).ToDictionary(number => number, number =>
                {
                    state.Issues.TryGetValue(number, out var issue);
                    var unmapped = NoInitialPullRequest && !heads.ContainsKey(number);
                    return new LiveIssueObservation
                    {
                        Ready = issue?.Tasks.Values.Any(task => task.LaunchConfirmed) != true,
                        PullRequest = unmapped ? null : number + (CompletionMismatch == "pr" ? 11 : 10),
                        HeadSha = unmapped ? null : CompletionMismatch == "head" ? new string('f', 40) : Head(number),
                        CompletedTaskId = CompletionMismatch == "task" ? null : issue?.ActiveTaskId,
                        Labels = TrackProjectedLabels ? ProjectedLabels.GetValueOrDefault(number, []) :
                            issue is null ? [] : OrchestrationReducer.ProjectLabels(issue).ToList()
                    };
                })
            });
        }

        public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(DeliveryStatus.Executing, store.Current.Outbox[intent.Id].Status);
            if (intent.Kind is not (EffectKind.ProjectLabels or EffectKind.ProjectStatus or EffectKind.PublishRequiredStatus) ||
                store.Current.Issues[intent.IssueNumber].Phase is not (Phase.HUMAN_REVIEW or Phase.BLOCKED or Phase.CLOSED))
                Assert.Equal(intent.IssueNumber, store.Current.AdmittedIssue);
            Executions.Add(intent.Kind);
            Deliveries.Add((intent.IssueNumber, intent.Kind, store.Current.AdmittedIssue, intent.StatusBody));
            if (TrackProjectedLabels && intent.Kind == EffectKind.ProjectLabels)
                ProjectedLabels[intent.IssueNumber] = intent.Labels.ToList();
            if (intent.Kind == EffectKind.LaunchTask)
            {
                LaunchedIssues.Add(intent.IssueNumber);
                heads[intent.IssueNumber] = new string((char)('a' + LaunchedIssues.Count), 40);
            }
            if (intent.Kind is EffectKind.RunCi or EffectKind.RequestReview)
            {
                var task = store.Current.Issues[intent.IssueNumber].Tasks[intent.TaskId!];
                Assert.Equal(TaskStatus.Completed, task.Status);
                Assert.Equal(Head(intent.IssueNumber), intent.Sha);
                Assert.Equal(task.FinalSha, intent.Sha);
            }
            if (intent.Kind == EffectKind.RunCi && ConfirmCiDispatch)
            {
                var reducer = new OrchestrationReducer();
                var prepared = new CiDispatchBoundary(101, new string('d', 40),
                    "refs/tags/agent-status-revocation-definition-v2", $"correlation:{intent.Id}", Now);
                var snapshot = await store.LoadAsync(cancellationToken);
                var state = reducer.RecordCiBoundary(snapshot.State, intent.Id, prepared);
                Assert.True(await store.TrySaveAsync(snapshot, state, cancellationToken));
                snapshot = await store.LoadAsync(cancellationToken);
                state = reducer.RecordCiBoundary(snapshot.State, intent.Id, prepared with { RunId = 777 });
                Assert.True(await store.TrySaveAsync(snapshot, state, cancellationToken));
            }
            if (BeforeExternalResult is not null) await BeforeExternalResult();
            var outcome = new EffectOutcome(DeliveryStatus.Succeeded,
                intent.Kind == EffectKind.RunCi && ConfirmCiDispatch ? "777" : $"external:{intent.Id}");
            outcomes[intent.Id] = outcome;
            AfterExecute?.Invoke();
            return UnknownLaunch && intent.Kind == EffectKind.LaunchTask || intent.Kind == UnknownProjection
                ? new EffectOutcome(DeliveryStatus.Unknown) : outcome;
        }

        public Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Recoveries++;
            return Task.FromResult(RecoveryUnknown ? new(DeliveryStatus.Unknown) : outcomes.GetValueOrDefault(intent.Id, new(DeliveryStatus.Unknown)));
        }

        private string Head(int number) => heads.GetValueOrDefault(number, new string('a', 40));
        private string? UnpublishedId(string intentId) =>
            IncludeUnpublishedResults && outcomes.TryGetValue(intentId, out var outcome) ? outcome.ExternalId : null;
    }
}
