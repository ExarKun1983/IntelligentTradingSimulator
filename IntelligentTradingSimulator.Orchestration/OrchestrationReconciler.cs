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

// Only deliberately injected offline fakes may simulate agent launch effects.
public interface IOfflineEffectAdapter : IEffectAdapter { }
public interface IVerifiedAgentEffectAdapter : IEffectAdapter
{
    bool AgentEffectsAuthorized { get; }
}

public sealed record ReconcileOptions(
    bool DryRun = true,
    bool LiveEffectsEnabled = false,
    bool PrerequisiteCapabilitiesVerified = false,
    bool CostApproved = false,
    int MaximumConflicts = 8,
    bool AllowOfflineAgentSimulation = false);

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

    public async Task<RepositoryState?> RecoverInFlightAsync(IStateStore store, IEffectAdapter effects,
        DateTimeOffset observedAt, ReconcileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaximumConflicts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        if (!EffectsEnabled(options)) return null;
        var snapshot = await store.LoadAsync(cancellationToken);
        var intent = InFlight(snapshot.State);
        return intent is null ? null : await RecoverIntentAsync(store, effects, intent, observedAt,
            options.MaximumConflicts, cancellationToken);
    }

    public async Task<ReconcileResult> RunAsync(IStateStore store, IObservationSource observations, IEffectAdapter effects,
        ReconcileOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaximumConflicts < 1) throw new ArgumentOutOfRangeException(nameof(options));
        var enabled = EffectsEnabled(options);
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

            var executing = InFlight(snapshot.State, includeUncertainStatuses: false);
            if (executing is not null)
            {
                // Both in-flight and uncertain intents are lookup-only; neither can be executed again.
                var resolved = await RecoverIntentAsync(store, effects, executing, live.ObservedAt,
                    options.MaximumConflicts, cancellationToken);
                return new(resolved, decision.PendingEffects, false);
            }
            var simulateAgents = options.AllowOfflineAgentSimulation && effects is IOfflineEffectAdapter ||
                effects is IVerifiedAgentEffectAdapter { AgentEffectsAuthorized: true };
            var pending = snapshot.State.Outbox.Values.FirstOrDefault(e => e.Status == DeliveryStatus.Pending &&
                (e.Kind is not (EffectKind.LaunchTask or EffectKind.RequestReview) || simulateAgents) &&
                !snapshot.State.Outbox.Values.Any(IsExclusiveUncertain) &&
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
            var final = await PublishOutcomeAsync(store, pending.Id, pending.Digest, outcome,
                live.ObservedAt, options.MaximumConflicts, cancellationToken);
            return new(final, decision.PendingEffects, false);
        }
        throw new InvalidOperationException("State publication conflict limit exceeded.");
    }

    private static bool EffectsEnabled(ReconcileOptions options) =>
        !options.DryRun && options.LiveEffectsEnabled && options.PrerequisiteCapabilitiesVerified && options.CostApproved;

    public static bool IsExclusiveUncertain(EffectIntent effect) =>
        effect.Status == DeliveryStatus.Unknown && effect.Kind != EffectKind.PublishRequiredStatus;

    private static EffectIntent? InFlight(RepositoryState state, bool includeUncertainStatuses = true) =>
        state.Outbox.Values.FirstOrDefault(effect => effect.Status == DeliveryStatus.Executing) ??
        state.Outbox.Values.FirstOrDefault(IsExclusiveUncertain) ??
        (includeUncertainStatuses ? state.Outbox.Values.FirstOrDefault(effect =>
            effect.Status == DeliveryStatus.Unknown && effect.Kind == EffectKind.PublishRequiredStatus) : null);

    private async Task<RepositoryState> RecoverIntentAsync(IStateStore store, IEffectAdapter effects,
        EffectIntent intent, DateTimeOffset observedAt, int maximumConflicts, CancellationToken cancellationToken)
    {
        EffectOutcome recovered;
        try
        {
            recovered = await effects.ReconcileAsync(intent, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            recovered = new(DeliveryStatus.Unknown);
        }
        return await PublishOutcomeAsync(store, intent.Id, intent.Digest, recovered,
            observedAt, maximumConflicts, cancellationToken);
    }

    private async Task<RepositoryState> PublishOutcomeAsync(IStateStore store, string id, string digest,
        EffectOutcome outcome, DateTimeOffset now, int maximumConflicts, CancellationToken cancellationToken)
    {
        for (var publish = 0; publish < maximumConflicts; publish++)
        {
            var latest = await store.LoadAsync(cancellationToken);
            if (!latest.State.Outbox.TryGetValue(id, out var intent) || intent.Digest != digest)
                throw new InvalidOperationException("Persisted operation identity changed during reconciliation.");
            if (intent.Status is not (DeliveryStatus.Executing or DeliveryStatus.Unknown or DeliveryStatus.Succeeded))
                return latest.State;
            var final = reducer.RecordOutcome(latest.State, id, outcome, now);
            if (StateJson.Serialize(final) == StateJson.Serialize(latest.State)) return latest.State;
            if (await store.TrySaveAsync(latest, final, cancellationToken)) return final;
        }
        throw new InvalidOperationException("Effect result publication conflict; intent retained for lookup-only recovery.");
    }

    private static bool IsSafeToExecute(RepositoryState state, EffectIntent intent, LiveObservation observation)
    {
        var issue = state.Issues[intent.IssueNumber];
        if (intent.Generation != issue.Generation ||
            !observation.Issues.TryGetValue(issue.Number, out var live)) return false;
        if (intent.Kind == EffectKind.PublishRequiredStatus)
            return RequiredStatusPolicy.Matches(state, issue, intent) && issue.PullRequest == live.PullRequest &&
                issue.HeadSha == live.HeadSha &&
                (intent.RequiredStatus?.State != "success" || !live.Closed);
        if (intent.Kind is EffectKind.ProjectLabels or EffectKind.ProjectStatus)
            return (state.AdmittedIssue == issue.Number || state.AdmittedIssue is null &&
                    issue.Phase is Phase.HUMAN_REVIEW or Phase.BLOCKED or Phase.CLOSED) &&
                (intent.Kind == EffectKind.ProjectLabels
                    ? intent.Labels.SequenceEqual(OrchestrationReducer.ProjectLabels(issue))
                    : intent.StatusBody == StatusProjection.Render(issue));
        var matchesCompletedTask = intent.TaskId is { } taskId && issue.ActiveTaskId == taskId &&
            issue.Tasks.TryGetValue(taskId, out var task) && task.LaunchConfirmed && task.Status == TaskStatus.Completed &&
            task.FinalSha == intent.Sha && task.Round == intent.Round;
        var eligible = intent.Kind switch
        {
            EffectKind.LaunchTask => issue.ActiveTaskId == intent.TaskId && intent.TaskId is { } launchTaskId &&
                issue.Tasks.TryGetValue(launchTaskId, out var launchTask) && launchTask.Status == TaskStatus.Intended &&
                launchTask.Round == intent.Round && LaunchSourceMatches(issue, launchTask, intent) &&
                issue.PendingFeedbackEventIds.All(id => launchTask.FeedbackEventIds.Contains(id)),
            EffectKind.RunCi => issue.PendingFeedbackEventIds.Count == 0 &&
                issue.Phase == Phase.CI_PENDING && issue.Ci is { Green: null } ci &&
                ci.IntentId == intent.Id && ci.TaskId == intent.TaskId && ci.Round == intent.Round && ci.Sha == intent.Sha,
            EffectKind.RequestReview => issue.PendingFeedbackEventIds.Count == 0 &&
                issue.Phase == Phase.REVIEW_PENDING && issue.Ci?.Green == true &&
                issue.Ci.TaskId == intent.TaskId && issue.Ci.Sha == intent.Sha && issue.Ci.Round == intent.Round &&
                issue.Review is { Classification: null } review && review.IntentId == intent.Id,
            _ => false
        };
        return eligible && (intent.Kind == EffectKind.LaunchTask || matchesCompletedTask) &&
            state.AdmittedIssue == issue.Number && issue.ActiveAuthorized &&
            (!OrchestrationReducer.RequiresInitialReadiness(issue) || live.Ready) && !live.Closed &&
            !issue.Paused && !issue.ReactivationRequired && issue.PullRequest == live.PullRequest &&
            intent.Sha == live.HeadSha && intent.Sha == issue.HeadSha;
    }

    private static bool LaunchSourceMatches(IssueState issue, TaskIdentity task, EffectIntent intent) =>
        task.LaunchSource switch
        {
            TaskLaunchSource.UnmappedIssue => task.Kind == TaskKind.Coding && task.ParentTaskId is null &&
                task.SourceSha is null && intent.Sha is null && issue.PullRequest is null && issue.HeadSha is null,
            TaskLaunchSource.PullRequestHead => issue.PullRequest is > 0 &&
                task.SourceSha is { Length: 40 or 64 } sha && sha.All(Uri.IsHexDigit) && sha == intent.Sha,
            _ => false
        };
}

public static class UpstreamIdentity
{
    public static Actor RestUser(JsonElement payload, ActorKind kind) => Parse(payload, "user", "type", kind, false);
    public static Actor GraphQlAuthor(JsonElement payload, ActorKind kind) => Parse(payload, "author", "__typename", kind, true);
    public static ReviewClassification ClassifyUnstructuredReview(string _) => ReviewClassification.NeedsHuman;

    private static Actor Parse(JsonElement payload, string property, string typeProperty, ActorKind kind, bool graphQl)
    {
        if (!payload.TryGetProperty(property, out var identity) || identity.ValueKind != JsonValueKind.Object ||
            !identity.TryGetProperty("login", out var login) || login.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(login.GetString()) ||
            !identity.TryGetProperty(typeProperty, out var type) || type.ValueKind != JsonValueKind.String ||
            type.GetString() is not ("User" or "Bot") || kind == ActorKind.Human && type.GetString() != "User")
            throw new InvalidDataException("Missing authenticated API author identity.");
        long? numericId = null;
        if (identity.TryGetProperty(graphQl ? "databaseId" : "id", out var databaseId) && databaseId.ValueKind != JsonValueKind.Null)
        {
            if (databaseId.ValueKind != JsonValueKind.Number || !databaseId.TryGetInt64(out var number) || number <= 0)
                throw new InvalidDataException("Invalid numeric API author identity.");
            numericId = number;
        }
        if (!graphQl && numericId is null) throw new InvalidDataException("Missing numeric REST author identity.");
        string? nodeId = null;
        if (identity.TryGetProperty(graphQl ? "id" : "node_id", out var node))
        {
            if (node.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetString()))
                throw new InvalidDataException("Invalid API author node identity.");
            nodeId = node.GetString();
        }
        if (graphQl && nodeId is null) throw new InvalidDataException("Missing GraphQL author node identity.");
        return new(login.GetString()!, kind, numericId, nodeId, type.GetString());
    }
}
