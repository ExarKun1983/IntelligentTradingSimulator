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
}

public enum WorkerStopReason { DryRun, EventBacklog, LeaseHeld, EffectsDisabled, Settled, Blocked, IterationLimit, TimeLimit, EffectLimit }

public sealed record WorkerResult(WorkerStopReason Reason, int Iterations, ReconcileResult? LastReconciliation);

/// <summary>Bounded host loop; all decisions and effect recovery still use the shared reconciler.</summary>
public sealed class OrchestrationWorker(OrchestrationReducer? reducer = null, TimeProvider? timeProvider = null)
{
    private readonly OrchestrationReducer reducer = reducer ?? new OrchestrationReducer();
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<WorkerResult> RunAsync(IStateStore store, IWorkerEventSource events,
        IObservationSource observations, IEffectAdapter effects, ReconcileOptions? reconcileOptions = null,
        WorkerOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        reconcileOptions ??= new();
        if (options.MaximumIterations < 1 || options.MaximumEventsPerIteration is < 1 or > OrchestrationReducer.MaximumIndexEntries ||
            options.MaximumDuration <= TimeSpan.Zero || options.MaximumDuration > TimeSpan.FromMinutes(30) ||
            options.PollInterval < TimeSpan.Zero || options.PollInterval > options.MaximumDuration)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (reconcileOptions.MaximumConflicts < 1 || reconcileOptions.MaximumEffectExecutions is < 1)
            throw new ArgumentOutOfRangeException(nameof(reconcileOptions));

        var reconciler = new OrchestrationReconciler(reducer);
        var started = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(options.MaximumDuration, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        ReconcileResult? last = null;
        var iterations = 0;
        var executedEffects = 0;
        var owner = Guid.NewGuid().ToString("N");
        var ownsLease = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reconcileOptions.DryRun)
            {
                ownsLease = await AcquireLeaseAsync(store, owner,
                    timeProvider.GetUtcNow() + options.MaximumDuration + TimeSpan.FromMinutes(1),
                    reconcileOptions.MaximumConflicts, token);
                if (!ownsLease) return new(WorkerStopReason.LeaseHeld, 0, null);
            }
            for (; iterations < options.MaximumIterations;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeProvider.GetElapsedTime(started) >= options.MaximumDuration)
                    return new(WorkerStopReason.TimeLimit, iterations, last);
                token.ThrowIfCancellationRequested();
                iterations++;
                // A completion can arrive before the launch/run result was saved.
                // Restore only recorded identities here; never execute new effects ahead of commands.
                var recovered = await reconciler.RecoverInFlightAsync(store, effects,
                    timeProvider.GetUtcNow(), reconcileOptions, token);
                var snapshot = await store.LoadAsync(token);
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
                    var decision = reconciler.Decide(state, await observations.ObserveAsync(state, token));
                    last = new(decision.State, decision.PendingEffects, true);
                    return new(WorkerStopReason.DryRun, iterations, last);
                }

                // Dispatch individually: committed events survive a later event/API/publication failure.
                var current = snapshot.State;
                foreach (var message in messages)
                {
                    if (AwaitingOperationIdentity(current, message)) continue;
                    current = await reconciler.DispatchAsync(store, message, token);
                }
                // Later pages may contain a pause or feedback; do not authorize an effect on a partial inbox.
                if (batch!.HasMore) continue;
                if (recovered is not null &&
                    current.Outbox.Values.Any(OrchestrationReconciler.IsExclusiveUncertain))
                {
                    last = new(current, current.Outbox.Values.Where(effect => effect.Status == DeliveryStatus.Pending).ToList(), false);
                    return new(WorkerStopReason.Blocked, iterations, last);
                }
                token.ThrowIfCancellationRequested();
                if (timeProvider.GetElapsedTime(started) >= options.MaximumDuration)
                    return new(WorkerStopReason.TimeLimit, iterations, last);

                // Re-fetch live guards after event publication, including after any CAS conflict.
                last = await reconciler.RunAsync(store, observations, effects, reconcileOptions, token);
                if (last.EffectsDisabled) return new(WorkerStopReason.EffectsDisabled, iterations, last);
                if (last.State.Outbox.Values.Any(OrchestrationReconciler.IsExclusiveUncertain))
                    return new(WorkerStopReason.Blocked, iterations, last);
                if (last.ExecutedEffect is not null &&
                    ++executedEffects >= reconcileOptions.MaximumEffectExecutions)
                    return new(WorkerStopReason.EffectLimit, iterations, last);
                if (!OrchestrationContinuation.HasWork(last.State, reconcileOptions))
                    return new(last.State.Issues.Values.Any(issue => issue.Phase == Phase.BLOCKED)
                        ? WorkerStopReason.Blocked : WorkerStopReason.Settled, iterations, last);

                // Progress drains immediately; unchanged state polls with backoff, not repeated launches.
                if (StateJson.Serialize(snapshot.State) == StateJson.Serialize(last.State))
                {
                    var remaining = options.MaximumDuration - timeProvider.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero) return new(WorkerStopReason.TimeLimit, iterations, last);
                    if (iterations < options.MaximumIterations && options.PollInterval > TimeSpan.Zero)
                        await Task.Delay(options.PollInterval < remaining ? options.PollInterval : remaining, timeProvider, token);
                }
            }
            return new(WorkerStopReason.IterationLimit, iterations, last);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // In-flight intents remain executing for lookup-only recovery; cancellation never rolls them back.
            return new(WorkerStopReason.TimeLimit, iterations, last);
        }
        finally
        {
            if (ownsLease)
            {
                // Cleanup is separately bounded even when the invocation was cancelled. A failed
                // release leaves a durable lease to expire, never a cleared admission/task intent.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
                await ReleaseLeaseAsync(store, owner, reconcileOptions, cleanup.Token);
            }
        }
    }

    private async Task<bool> AcquireLeaseAsync(IStateStore store, string owner, DateTimeOffset expiresAt,
        int maximumConflicts, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < maximumConflicts; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            if (snapshot.State.WorkerLease is { } lease)
            {
                if (string.IsNullOrWhiteSpace(lease.Owner) || lease.ExpiresAt == default)
                    throw new InvalidDataException("Invalid persisted worker lease; reconciliation is required.");
                if (lease.ExpiresAt > timeProvider.GetUtcNow()) return false;
            }
            // Commit recovery responsibility before observations or external effects can begin.
            var state = OrchestrationContinuation.Prepare(snapshot.State, timeProvider.GetUtcNow(), "Invocation in flight.", true);
            state.WorkerLease = new(owner, expiresAt);
            if (await store.TrySaveAsync(snapshot, state, cancellationToken)) return true;
        }
        throw new InvalidOperationException("Worker lease publication conflict limit exceeded.");
    }

    private async Task ReleaseLeaseAsync(IStateStore store, string owner,
        ReconcileOptions options, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < options.MaximumConflicts; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            if (snapshot.State.WorkerLease?.Owner != owner) return;
            var state = OrchestrationContinuation.Prepare(snapshot.State, timeProvider.GetUtcNow(),
                OrchestrationContinuation.HasWork(snapshot.State, options) ? "Automatic work remains." : "Invocation settled or human-held.",
                options: options);
            state.WorkerLease = null;
            if (await store.TrySaveAsync(snapshot, state, cancellationToken)) return;
        }
        throw new InvalidOperationException("Worker lease release conflict; wait for its recorded expiry.");
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
