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

    [Fact]
    public async Task IsolatedEffectBudgetStopsAfterCodingCiAndSuccessWithoutReviewOrReplay()
    {
        var store = new Store();
        var api = new OfflineApi(store) { SinglePassReady = true, ConfirmCiDispatch = true };
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

        Assert.Equal(WorkerStopReason.EffectLimit, result.Reason);
        Assert.Equal(new[]
        {
            EffectKind.LaunchTask, EffectKind.RunCi, EffectKind.PublishRequiredStatus
        }, api.Executions);
        Assert.Single(store.Current.Issues[7].Tasks);
        Assert.Equal(Phase.REVIEW_PENDING, store.Current.Issues[7].Phase);
        Assert.Null(store.Current.Issues[7].Review);
        Assert.Equal("success", store.Current.Outbox.Values.Single(effect =>
            effect.Kind == EffectKind.PublishRequiredStatus &&
            effect.RequiredStatus?.State == "success").RequiredStatus!.State);

        var executionCount = api.Executions.Count;
        await worker.RunAsync(store, api, api, api, reconcile, Fast);
        Assert.Equal(executionCount, api.Executions.Count);
        Assert.DoesNotContain(EffectKind.RequestReview, api.Executions);
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

    private sealed class Clock : TimeProvider
    {
        private long ticks;
        private readonly List<Timer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => Now.AddTicks(ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
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
        public List<int> LaunchedIssues { get; } = [];
        public int Recoveries { get; private set; }
        public int CiFailures { get; init; }
        public int ReviewBlockers { get; init; }
        public int[] ReadyIssues { get; init; } = [7];
        public bool CompleteTasks { get; set; } = true;
        public bool SinglePassReady { get; init; }
        public bool ConfirmCiDispatch { get; init; }
        public bool NoInitialPullRequest { get; init; }
        public string? CompletionMismatch { get; set; }
        public bool UnknownLaunch { get; init; }
        public bool RecoveryUnknown { get; init; }
        public bool IncludeUnpublishedResults { get; init; }
        public bool FailObservation { get; set; }
        public bool AmbiguousFirstReview { get; init; }
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
                if (issue.Ci is { Green: null } ci &&
                    (ci.RunId ?? UnpublishedId(ci.IntentId)) is { } runId)
                {
                    var green = issue.Tasks.Values.Count(task => task.Kind == TaskKind.CiRepair) >= CiFailures;
                    Add(new CiResult(runId, ci.TaskId, ci.Round, number + 10, ci.Sha, green,
                        green ? [] : [new("ci:compile", "Compiler failure", "Build job compiler diagnostic")]),
                        $"ci:{runId}", number);
                }
                if (issue.Review is { Classification: null } review &&
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
                Issues = ReadyIssues.ToDictionary(number => number, number =>
                {
                    state.Issues.TryGetValue(number, out var issue);
                    var unmapped = NoInitialPullRequest && !heads.ContainsKey(number);
                    return new LiveIssueObservation
                    {
                        Ready = issue?.Tasks.Values.Any(task => task.LaunchConfirmed) != true,
                        PullRequest = unmapped ? null : number + (CompletionMismatch == "pr" ? 11 : 10),
                        HeadSha = unmapped ? null : CompletionMismatch == "head" ? new string('f', 40) : Head(number),
                        CompletedTaskId = CompletionMismatch == "task" ? null : issue?.ActiveTaskId,
                        Labels = issue is null ? [] : OrchestrationReducer.ProjectLabels(issue).ToList()
                    };
                })
            });
        }

        public async Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(DeliveryStatus.Executing, store.Current.Outbox[intent.Id].Status);
            Assert.Equal(intent.IssueNumber, store.Current.AdmittedIssue);
            Executions.Add(intent.Kind);
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
                    "refs/tags/agent-status-revocation-definition-v1", $"correlation:{intent.Id}", Now);
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
            return UnknownLaunch && intent.Kind == EffectKind.LaunchTask
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
