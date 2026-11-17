namespace IntelligentTradingSimulator.Orchestration;

public interface IWorkerEventSource
{
    // Return validated events with stable IDs/content until their inbox entries are committed.
    // A notification or a successful read is not acknowledgement of durable acceptance.
    Task<WorkerEventBatch> ReadAsync(RepositoryState state, CancellationToken cancellationToken);
}

public sealed record WorkerEventBatch(IReadOnlyList<OrchestrationEvent> Events, bool HasMore = false);

public sealed record WorkerOptions
{
    public int MaximumIterations { get; init; } = 128;
    public int MaximumEventsPerIteration { get; init; } = 128;
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
    public bool TrackAutomaticContinuations { get; init; }
}

public enum WorkerStopReason { DryRun, EventBacklog, LeaseHeld, EffectsDisabled, Settled, Blocked, IterationLimit, TimeLimit, EffectLimit, ContinuationLimit }

public sealed record WorkerResult(WorkerStopReason Reason, int Iterations, ReconcileResult? LastReconciliation)
{
    internal int? LeaseAdmittedIssue { get; init; }
    internal bool HoldBarrierReleased { get; init; }
}

/// <summary>Bounded host loop; all decisions and effect recovery still use the shared reconciler.</summary>
public sealed class OrchestrationWorker(OrchestrationReducer? reducer = null, TimeProvider? timeProvider = null,
    GitHubLiveDiagnostics? diagnostics = null)
{
    private readonly OrchestrationReducer reducer = reducer ?? new OrchestrationReducer();
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public Task<T> RunExclusiveAsync<T>(IStateStore store, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) => RunExclusiveAsync(store, action, false, cancellationToken);

    internal Task<T> RunExclusiveWhenAvailableAsync<T>(IStateStore store, Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default) => RunExclusiveAsync(store, action, true, cancellationToken);

    private async Task<T> RunExclusiveAsync<T>(IStateStore store, Func<CancellationToken, Task<T>> action,
        bool waitForLease, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var started = timeProvider.GetTimestamp();
        var retries = 0;
        var callerStage = diagnostics?.Stage;
        diagnostics?.Mark(GitHubLiveStage.WorkerLease);
        while (true)
        {
            var acquired = await AcquireLeaseAsync(store, owner, timeProvider.GetUtcNow() + TimeSpan.FromMinutes(31),
                8, false, true, cancellationToken);
            if (acquired.Acquired) break;
            if (!waitForLease)
                throw new InvalidOperationException("Worker lease is occupied; retry recovery after the active invocation finishes.");
            var elapsed = timeProvider.GetElapsedTime(started);
            if (elapsed >= LeaseWait.Limit)
                throw new InvalidOperationException($"Worker lease remained occupied for {elapsed:c} (limit {LeaseWait.Limit:c}); exclusive operation needs operator attention.");
            await Task.Delay(LeaseWait.NextDelay(retries++, LeaseWait.Limit - elapsed), timeProvider, cancellationToken);
        }
        if (callerStage is { } previousStage) diagnostics?.Mark(previousStage);
        Exception? actionFailure = null;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            return await action(linked.Token);
        }
        catch (Exception exception)
        {
            diagnostics?.Capture(exception);
            actionFailure = exception;
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
            diagnostics?.Mark(GitHubLiveStage.WorkerLeaseRelease);
            try
            {
                await ReleaseLeaseAsync(store, owner, new ReconcileOptions(), false, cleanup.Token);
            }
            catch (Exception cleanupFailure) when (actionFailure is not null)
            {
                diagnostics?.MarkLeaseReleaseFailure();
                throw new InvalidOperationException("Exclusive action and worker lease release both failed.",
                    new AggregateException(actionFailure, cleanupFailure));
            }
        }
    }

    public async Task<WorkerResult> RunAsync(IStateStore store, IWorkerEventSource events,
        IObservationSource observations, IEffectAdapter effects, ReconcileOptions? reconcileOptions = null,
        WorkerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        reconcileOptions ??= new();
        if (options.TrackAutomaticContinuations && !reconcileOptions.ProjectionOnly)
            reconcileOptions = reconcileOptions with { PrioritizeAdmittedWork = true };
        if (options.MaximumIterations < 1 || options.MaximumEventsPerIteration is < 1 or > OrchestrationReducer.MaximumIndexEntries ||
            options.MaximumDuration <= TimeSpan.Zero || options.MaximumDuration > TimeSpan.FromMinutes(30) ||
            options.PollInterval < TimeSpan.Zero || options.PollInterval > options.MaximumDuration)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (reconcileOptions.MaximumConflicts < 1 || reconcileOptions.MaximumEffectExecutions is < 1)
            throw new ArgumentOutOfRangeException(nameof(reconcileOptions));

        var reconciler = new OrchestrationReconciler(reducer, diagnostics);
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(options.MaximumDuration, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        ReconcileResult? last = null;
        var iterations = 0;
        var executedEffects = 0;
        var owner = Guid.NewGuid().ToString("N");
        var ownsLease = false;
        int? leaseAdmittedIssue = null;
        var yielded = false;
        var interrupted = false;
        var holdBarrierReleased = false;
        var interruptionSaved = false;
        var interruptionCleanupStarted = false;
        async Task PreserveInterruptedLinksAsync()
        {
            interruptionCleanupStarted = true;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45), timeProvider);
            var state = await reconciler.InterruptIssueLinksAsync(store, reconcileOptions,
                timeProvider.GetUtcNow(), cleanup.Token);
            if (CanRevoke(state, reconcileOptions) && effects is IIndependentStatusRevocation revocation)
                state = await revocation.RevokeRequiredStatusesAsync(store, reconcileOptions,
                    timeProvider.GetUtcNow(), cleanup.Token);
            if (last is not null) last = last with { State = state };
            interruptionSaved = true;
        }
        WorkerResult Yield(WorkerStopReason reason)
        {
            yielded = true;
            return new(reason, iterations, last)
                { LeaseAdmittedIssue = leaseAdmittedIssue, HoldBarrierReleased = holdBarrierReleased };
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reconcileOptions.DryRun)
            {
                diagnostics?.Mark(GitHubLiveStage.WorkerLease);
                var acquisition = await AcquireLeaseAsync(store, owner,
                    timeProvider.GetUtcNow() + options.MaximumDuration + TimeSpan.FromMinutes(1),
                    reconcileOptions.MaximumConflicts, options.TrackAutomaticContinuations, false, token);
                ownsLease = acquisition.Acquired;
                leaseAdmittedIssue = acquisition.AdmittedIssue;
                if (!ownsLease) return new(WorkerStopReason.LeaseHeld, 0, null);
                if (acquisition.ContinuationLimitReached)
                    return new(WorkerStopReason.ContinuationLimit, 0, null) { LeaseAdmittedIssue = leaseAdmittedIssue };
            }
            for (; iterations < options.MaximumIterations;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeProvider.GetElapsedTime(started) >= options.MaximumDuration)
                    return Yield(WorkerStopReason.TimeLimit);
                token.ThrowIfCancellationRequested();
                iterations++;
                // A completion can arrive before the launch/run result was saved.
                // Restore only recorded identities here; never execute new effects ahead of commands.
                diagnostics?.Mark(GitHubLiveStage.InFlightRecovery);
                var recovered = await reconciler.RecoverInFlightAsync(store, effects,
                    timeProvider.GetUtcNow(), reconcileOptions, token);
                diagnostics?.Mark(GitHubLiveStage.WorkerStateLoad);
                var snapshot = await store.LoadAsync(token);
                if (CanRevoke(snapshot.State, reconcileOptions) && effects is IIndependentStatusRevocation afterRecovery)
                {
                    await afterRecovery.RevokeRequiredStatusesAsync(store, reconcileOptions, timeProvider.GetUtcNow(), token);
                    snapshot = await store.LoadAsync(token);
                }
                diagnostics?.Mark(GitHubLiveStage.WorkerEventRead);
                var batch = await events.ReadAsync(snapshot.State, token);
                var messages = batch?.Events;
                if (messages is null || messages.Count > options.MaximumEventsPerIteration)
                    throw new InvalidDataException("Observation event batch exceeds the worker limit.");

                if (reconcileOptions.DryRun)
                {
                    var state = snapshot.State;
                    foreach (var message in messages)
                        if (!AwaitingOperationIdentity(state, message))
                            state = reducer.Apply(state, message);
                    if (batch!.HasMore || messages.Any(message => AwaitingOperationIdentity(state, message)))
                    {
                        last = new(state, [], true);
                        return new(WorkerStopReason.EventBacklog, iterations, last);
                    }
                    var decision = reconciler.Decide(state, await observations.ObserveAsync(state, token), reconcileOptions);
                    last = new(decision.State, decision.PendingEffects, true);
                    return new(WorkerStopReason.DryRun, iterations, last);
                }

                // Dispatch individually: committed events survive a later event/API/publication failure.
                var current = snapshot.State;
                diagnostics?.Mark(GitHubLiveStage.EventPublication);
                foreach (var message in messages)
                {
                    if (AwaitingOperationIdentity(current, message)) continue;
                    current = await reconciler.DispatchAsync(store, message, token);
                }
                // Later pages may contain a pause or feedback; do not authorize an effect on a partial inbox.
                if (batch!.HasMore) continue;
                if (current.Issues.Values.Any(issue =>
                    RequiredStatusPolicy.CaptureFinalApproval(current, issue, requirePublication: false) is
                        { PublicationConfirmedAt: null }))
                    current = await reconciler.ConfirmFinalApprovalsAsync(store, timeProvider, token);
                if (CanRevoke(current, reconcileOptions) && effects is IIndependentStatusRevocation beforeStop)
                    current = await beforeStop.RevokeRequiredStatusesAsync(store, reconcileOptions, timeProvider.GetUtcNow(), token);
                if (recovered is not null &&
                    current.Outbox.Values.Any(OrchestrationReconciler.IsExclusiveUncertain))
                {
                    last = new(current, current.Outbox.Values.Where(effect => effect.Status == DeliveryStatus.Pending).ToList(), false);
                    return new(WorkerStopReason.Blocked, iterations, last);
                }
                token.ThrowIfCancellationRequested();
                if (timeProvider.GetElapsedTime(started) >= options.MaximumDuration)
                    return Yield(WorkerStopReason.TimeLimit);

                // Re-fetch live guards after event publication, including after any CAS conflict.
                last = await reconciler.RunAsync(store, observations, effects, reconcileOptions, token);
                if (!last.EffectsDisabled && CanRevoke(last.State, reconcileOptions) &&
                    effects is IIndependentStatusRevocation afterEffect)
                {
                    var revoked = await afterEffect.RevokeRequiredStatusesAsync(store, reconcileOptions, timeProvider.GetUtcNow(), token);
                    last = last with { State = revoked };
                }
                holdBarrierReleased |= snapshot.State.Issues.Any(pair =>
                    pair.Value.ContinuationHoldBarrier is not null &&
                    last.State.Issues.TryGetValue(pair.Key, out var currentIssue) &&
                    currentIssue.ContinuationHoldBarrier is null);
                if (last.EffectsDisabled) return new(WorkerStopReason.EffectsDisabled, iterations, last);
                if (last.State.Outbox.Values.Any(OrchestrationReconciler.IsExclusiveUncertain))
                    return new(WorkerStopReason.Blocked, iterations, last);
                if (last.ExecutedEffect is not null &&
                    ++executedEffects >= reconcileOptions.MaximumEffectExecutions)
                    return new(WorkerStopReason.EffectLimit, iterations, last);
                if (last.ExecutedEffect is not null &&
                    last.State.Issues.Values.Any(issue => issue.ContinuationHoldBarrier is not null))
                    continue;
                if (!OrchestrationContinuation.HasWork(last.State, reconcileOptions))
                    return new(last.State.Issues.Values.Any(issue => issue.Phase == Phase.BLOCKED)
                        ? WorkerStopReason.Blocked : WorkerStopReason.Settled, iterations, last);

                // Progress drains immediately; unchanged state polls with backoff, not repeated launches.
                if (StateJson.Serialize(snapshot.State) == StateJson.Serialize(last.State))
                {
                    var remaining = options.MaximumDuration - timeProvider.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero) return Yield(WorkerStopReason.TimeLimit);
                    if (iterations < options.MaximumIterations && options.PollInterval > TimeSpan.Zero)
                        await Task.Delay(options.PollInterval < remaining ? options.PollInterval : remaining, timeProvider, token);
                }
            }
            return Yield(WorkerStopReason.IterationLimit);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            if (ownsLease) await PreserveInterruptedLinksAsync();
            return Yield(WorkerStopReason.TimeLimit);
        }
        catch (Exception exception)
        {
            diagnostics?.Capture(exception);
            interrupted = true;
            throw;
        }
        finally
        {
            if (ownsLease)
            {
                if (!interruptionCleanupStarted && (token.IsCancellationRequested || interrupted))
                    await PreserveInterruptedLinksAsync();
                // Cleanup is separately bounded even when the invocation was cancelled. A failed
                // release leaves a durable lease to expire, never a cleared admission/task intent.
                if (!interruptionCleanupStarted || interruptionSaved)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
                    diagnostics?.Mark(GitHubLiveStage.WorkerLeaseRelease);
                    await ReleaseLeaseAsync(store, owner, reconcileOptions,
                        options.TrackAutomaticContinuations && (yielded || interrupted), cleanup.Token);
                }
            }
        }
    }

    private static bool CanRevoke(RepositoryState state, ReconcileOptions options) =>
        !options.DryRun && options.LiveEffectsEnabled && options.PrerequisiteCapabilitiesVerified && options.CostApproved &&
        (options.AllowedEffectKinds is null || options.AllowedEffectKinds.Contains(EffectKind.PublishRequiredStatus)) &&
        (options.AllowedRequiredStatusStates is null || options.AllowedRequiredStatusStates.Contains("error")) &&
        state.Issues.Values.Any(issue =>
            (options.ProjectionIssueNumber is null || options.ProjectionIssueNumber == issue.Number) &&
            RequiredStatusPolicy.RequiresIssueLinkRevocation(state, issue));

    private async Task<(bool Acquired, bool ContinuationLimitReached, int? AdmittedIssue)> AcquireLeaseAsync(
        IStateStore store, string owner, DateTimeOffset expiresAt,
        int maximumConflicts, bool trackAutomaticContinuations, bool markInterruptedLease,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < maximumConflicts; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            OrchestrationContinuation.Validate(snapshot.State);
            if (snapshot.State.WorkerLease is { } lease)
            {
                if (string.IsNullOrWhiteSpace(lease.Owner) || lease.ExpiresAt == default)
                    throw new InvalidDataException("Invalid persisted worker lease; reconciliation is required.");
                if (lease.ExpiresAt > timeProvider.GetUtcNow()) return (false, false, null);
            }
            // Commit recovery responsibility before observations or external effects can begin.
            var state = OrchestrationContinuation.Prepare(snapshot.State, timeProvider.GetUtcNow(), "Invocation in flight.", true);
            var limitReached = false;
            if (ActiveTask(state) is { } task &&
                (trackAutomaticContinuations && task.ContinuationPending ||
                 (trackAutomaticContinuations || markInterruptedLease) && snapshot.State.WorkerLease is not null))
            {
                if (task.AutomaticContinuations == OrchestrationContinuation.MaxAutomaticContinuations)
                {
                    state = reducer.HoldForContinuationLimit(state, state.AdmittedIssue!.Value, task.Id);
                    limitReached = true;
                }
                else if (trackAutomaticContinuations)
                {
                    task.AutomaticContinuations++;
                    task.ContinuationPending = false;
                }
                else
                    task.ContinuationPending = true;
            }
            state.WorkerLease = new(owner, expiresAt);
            if (await store.TrySaveAsync(snapshot, state, cancellationToken))
                return (true, limitReached, snapshot.State.AdmittedIssue);
        }
        throw new InvalidOperationException("Worker lease publication conflict limit exceeded.");
    }

    private async Task ReleaseLeaseAsync(IStateStore store, string owner,
        ReconcileOptions options, bool yielded, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.MaximumConflicts; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            if (snapshot.State.WorkerLease?.Owner != owner) return;
            var input = snapshot.State;
            if (yielded && ActiveTask(input) is { } task)
            {
                var issue = input.Issues[input.AdmittedIssue!.Value];
                input = task.AutomaticContinuations == OrchestrationContinuation.MaxAutomaticContinuations
                    ? reducer.HoldForContinuationLimit(input, issue.Number, task.Id)
                    : StateJson.Clone(input);
                if (task.AutomaticContinuations < OrchestrationContinuation.MaxAutomaticContinuations)
                    input.Issues[issue.Number].Tasks[task.Id].ContinuationPending = true;
            }
            var state = OrchestrationContinuation.Prepare(input, timeProvider.GetUtcNow(),
                OrchestrationContinuation.HasWork(input, options) ? "Automatic work remains." : "Invocation settled or human-held.",
                options: options);
            state.WorkerLease = null;
            if (await store.TrySaveAsync(snapshot, state, cancellationToken)) return;
        }
        throw new InvalidOperationException("Worker lease release conflict; wait for its recorded expiry.");
    }

    internal static TaskIdentity? ActiveTask(RepositoryState state)
    {
        if (state.AdmittedIssue is not { } number ||
            !state.Issues.TryGetValue(number, out var issue) || !issue.ActiveAuthorized ||
            issue.Paused || issue.ReactivationRequired ||
            issue.Phase is not (Phase.CODING or Phase.CI_PENDING or Phase.REVIEW_PENDING or Phase.FIX_PENDING) ||
            issue.ActiveTaskId is not { } taskId || !issue.Tasks.TryGetValue(taskId, out var task) ||
            state.Outbox.Values.Any(OrchestrationReconciler.IsExclusiveUncertain))
            return null;
        return task;
    }

    private static bool AwaitingOperationIdentity(RepositoryState state, OrchestrationEvent message)
    {
        if (!state.Issues.TryGetValue(message.IssueNumber, out var issue)) return false;
        var intentId = message.Data switch
        {
            TaskCompleted completed when issue.Tasks.TryGetValue(completed.TaskId, out var task) && !task.LaunchConfirmed =>
                state.Outbox.Values.FirstOrDefault(effect => effect.TaskId == completed.TaskId && effect.Kind == EffectKind.LaunchTask)?.Id,
            CiResult when issue.Ci?.RunId is null => issue.Ci?.IntentId,
            ReviewResult when issue.Review?.ReviewId is null => issue.Review?.IntentId,
            _ => null
        };
        // Deferred events are not acknowledged. Their stable source identity must be redelivered on Resume.
        return intentId is not null && state.Outbox.TryGetValue(intentId, out var intent) &&
            intent.Status is DeliveryStatus.Executing or DeliveryStatus.Unknown;
    }
}
