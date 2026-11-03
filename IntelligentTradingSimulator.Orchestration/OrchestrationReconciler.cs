using System.Text.Json;

namespace IntelligentTradingSimulator.Orchestration;

public sealed record EffectOutcome(DeliveryStatus Status, string? ExternalId = null);

public interface IObservationSource
{
    Task<LiveObservation> ObserveAsync(RepositoryState state, CancellationToken cancellationToken);
}

public interface IEffectAdapter
{
    Task<EffectOutcome> ExecuteAsync(EffectIntent intent, CancellationToken cancellationToken);
    // Must look up the persisted idempotency key and operation identity, never launch a replacement.
    Task<EffectOutcome> ReconcileAsync(EffectIntent intent, CancellationToken cancellationToken);
}

public sealed record ReconcileOptions(
    bool DryRun = true,
    bool LiveEffectsEnabled = false,
    bool PrerequisiteCapabilitiesVerified = false,
    bool CostApproved = false,
    int MaximumConflicts = 8);

public sealed record ReconcileResult(RepositoryState State, IReadOnlyList<EffectIntent> Decisions, bool EffectsDisabled);

public sealed class OrchestrationReconciler(OrchestrationReducer? reducer = null)
{
    private readonly OrchestrationReducer reducer = reducer ?? new OrchestrationReducer();

    public ReconcileDecision Decide(RepositoryState state, LiveObservation observation) => reducer.Reconcile(state, observation);

    public async Task<RepositoryState> DispatchAsync(IStateStore store, OrchestrationEvent message,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            var state = reducer.Apply(snapshot.State, message);
            if (StateJson.Serialize(snapshot.State) == StateJson.Serialize(state)) return state;
            if (await store.TrySaveAsync(snapshot, state, cancellationToken)) return state;
        }
        throw new InvalidOperationException("State publication conflict limit exceeded.");
    }

    public async Task<ReconcileResult> RunAsync(IStateStore store, IObservationSource observations, IEffectAdapter effects,
        ReconcileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaximumConflicts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var enabled = !options.DryRun && options.LiveEffectsEnabled &&
            options.PrerequisiteCapabilitiesVerified && options.CostApproved;
        for (var attempt = 0; attempt < options.MaximumConflicts; attempt++)
        {
            var snapshot = await store.LoadAsync(cancellationToken);
            var live = await observations.ObserveAsync(snapshot.State, cancellationToken);
            var decision = reducer.Reconcile(snapshot.State, live);
            if (options.DryRun) return new(decision.State, decision.PendingEffects, true);
            if (StateJson.Serialize(snapshot.State) != StateJson.Serialize(decision.State))
            {
                if (!await store.TrySaveAsync(snapshot, decision.State, cancellationToken)) continue;
                snapshot = await store.LoadAsync(cancellationToken);
            }
            if (!enabled) return new(snapshot.State, decision.PendingEffects, true);

            var executing = snapshot.State.Outbox.Values.FirstOrDefault(e => e.Status == DeliveryStatus.Executing);
            if (executing is not null)
            {
                // Recovery is the only permitted operation on an executing intent. Unknown remains held.
                EffectOutcome recovered;
                try
                {
                    recovered = await effects.ReconcileAsync(executing, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    recovered = new(DeliveryStatus.Unknown);
                }
                var resolved = reducer.RecordOutcome(snapshot.State, executing.Id, recovered, live.ObservedAt);
                if (!await store.TrySaveAsync(snapshot, resolved, cancellationToken))
                    return new((await store.LoadAsync(cancellationToken)).State, decision.PendingEffects, false);
                return new(resolved, decision.PendingEffects, false);
            }
            // Launching coding or review agents is impossible in this release, even with flags.
            var pending = snapshot.State.Outbox.Values.FirstOrDefault(e => e.Status == DeliveryStatus.Pending &&
                e.Kind is not (EffectKind.LaunchTask or EffectKind.RequestReview) &&
                !snapshot.State.Outbox.Values.Any(other => other.Status == DeliveryStatus.Unknown) &&
                IsSafeToExecute(snapshot.State, e, live));
            if (pending is null) return new(snapshot.State, decision.PendingEffects, false);
            var marked = reducer.MarkExecuting(snapshot.State, pending.Id, live.ObservedAt);
            if (!await store.TrySaveAsync(snapshot, marked, cancellationToken)) continue;
            EffectOutcome outcome;
            try
            {
                outcome = await effects.ExecuteAsync(marked.Outbox[pending.Id], cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcome = new(DeliveryStatus.Unknown);
            }
            // Once execution started, a lost result must never cause another Execute call.
            for (var publish = 0; publish < options.MaximumConflicts; publish++)
            {
                var latest = await store.LoadAsync(cancellationToken);
                if (!latest.State.Outbox.TryGetValue(pending.Id, out var intent) ||
                    intent.Status != DeliveryStatus.Executing)
                    return new(latest.State, decision.PendingEffects, false);
                var final = reducer.RecordOutcome(latest.State, pending.Id, outcome, live.ObservedAt);
                if (await store.TrySaveAsync(latest, final, cancellationToken))
                    return new(final, decision.PendingEffects, false);
            }
            throw new InvalidOperationException("Effect result publication conflict; executing intent retained for recovery.");
        }
        throw new InvalidOperationException("State publication conflict limit exceeded.");
    }

    private static bool IsSafeToExecute(RepositoryState state, EffectIntent intent, LiveObservation observation)
    {
        var issue = state.Issues[intent.IssueNumber];
        if (intent.Generation != issue.Generation ||
            !observation.Issues.TryGetValue(issue.Number, out var live)) return false;
        if (intent.Kind == EffectKind.ProjectLabels)
            return (state.AdmittedIssue == issue.Number || state.AdmittedIssue is null &&
                    issue.Phase is Phase.HUMAN_REVIEW or Phase.BLOCKED or Phase.CLOSED) &&
                intent.Labels.SequenceEqual(OrchestrationReducer.ProjectLabels(issue));
        var matchesCompletedTask = intent.TaskId is { } taskId && issue.ActiveTaskId == taskId &&
            issue.Tasks.TryGetValue(taskId, out var task) && task.LaunchConfirmed && task.Status == TaskStatus.Completed &&
            task.FinalSha == intent.Sha && task.Round == intent.Round;
        var eligible = intent.Kind switch
        {
            EffectKind.RunCi => issue.Phase == Phase.CI_PENDING && issue.Ci is { Green: null } ci &&
                ci.IntentId == intent.Id && ci.TaskId == intent.TaskId && ci.Round == intent.Round && ci.Sha == intent.Sha,
            EffectKind.RequestReview => issue.Phase == Phase.REVIEW_PENDING && issue.Ci?.Green == true &&
                issue.Ci.TaskId == intent.TaskId && issue.Ci.Sha == intent.Sha && issue.Ci.Round == intent.Round &&
                issue.Review is { Classification: null } review && review.IntentId == intent.Id,
            _ => false
        };
        return eligible && matchesCompletedTask && state.AdmittedIssue == issue.Number && issue.Ready && live.Ready && !live.Closed &&
            !issue.Paused && !issue.ReactivationRequired && issue.PullRequest == live.PullRequest &&
            intent.Sha == live.HeadSha && intent.Sha == issue.HeadSha;
    }
}

public static class UpstreamIdentity
{
    public static Actor RestUser(JsonElement payload, ActorKind kind) => Parse(payload, "user", "id", kind);
    public static Actor GraphQlAuthor(JsonElement payload, ActorKind kind) => Parse(payload, "author", "databaseId", kind);
    public static ReviewClassification ClassifyUnstructuredReview(string _) => ReviewClassification.NeedsHuman;

    private static Actor Parse(JsonElement payload, string property, string idProperty, ActorKind kind)
    {
        if (!payload.TryGetProperty(property, out var identity) || identity.ValueKind != JsonValueKind.Object ||
            !identity.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(login.GetString()) ||
            !identity.TryGetProperty(idProperty, out var identityId) || identityId.ValueKind != JsonValueKind.Number ||
            !identityId.TryGetInt64(out var numericId) || numericId <= 0)
            throw new InvalidDataException("Missing authenticated API author identity.");
        return new(login.GetString()!, kind, numericId);
    }
}
