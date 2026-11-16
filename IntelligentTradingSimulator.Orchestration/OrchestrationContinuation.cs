namespace IntelligentTradingSimulator.Orchestration;

public sealed record ContinuationTicket(string Id, long RepositoryGeneration, DateTimeOffset DueAt,
    string Reason, bool Pending = true);

/// <summary>A scheduled wake consumes canonical work, not a synthetic lifecycle command.</summary>
public static class OrchestrationContinuation
{
    internal const int MaxAutomaticContinuations = 3;

    internal static bool IsPublishableHold(IssueState issue) => issue.ReactivationRequired &&
        (issue.Phase == Phase.BLOCKED ||
         issue.Phase == Phase.CLOSED && issue.TerminalOutcome is TerminalOutcome.Merged or TerminalOutcome.Unmerged);

    internal static bool HoldProjectionConfirmed(RepositoryState state, IssueState issue, bool requiredStatusEnabled)
    {
        var labels = OrchestrationReducer.ProjectLabels(issue);
        var labelsConfirmed = issue.ObservedProjectionDigest == StateJson.Digest(string.Join("\n", labels));
        var body = StatusProjection.Render(issue);
        var statusConfirmed = issue.StatusProjectionDigest == StateJson.Digest(body) &&
            state.Outbox.Values.Any(effect => effect.IssueNumber == issue.Number &&
                effect.Generation == issue.Generation && effect.Kind == EffectKind.ProjectStatus &&
                effect.Status == DeliveryStatus.Succeeded && effect.StatusBody == body);
        var requiredStatusConfirmed = !requiredStatusEnabled || issue.PullRequest is not > 0 ||
            issue.HeadSha is not { Length: 40 or 64 } ||
            issue.RequiredStatusIntentId is { } id && state.Outbox.TryGetValue(id, out var status) &&
            status.Status == DeliveryStatus.Succeeded && status.RequiredStatus?.State == "error" &&
            RequiredStatusPolicy.Matches(state, issue, status);
        return labelsConfirmed && statusConfirmed && requiredStatusConfirmed;
    }

    public static void Validate(RepositoryState state)
    {
        if (state.Continuation is { } ticket && (string.IsNullOrWhiteSpace(ticket.Id) || ticket.Id.Length > 256 ||
                ticket.RepositoryGeneration != state.Generation || ticket.DueAt == default ||
                string.IsNullOrWhiteSpace(ticket.Reason) || ticket.Reason.Length > 2048) ||
            state.WorkerLease is { } lease && (string.IsNullOrWhiteSpace(lease.Owner) || lease.Owner.Length > 256 ||
                lease.ExpiresAt == default) ||
            state.Issues.Values.SelectMany(issue => issue.Tasks.Values)
                .Any(task => task.AutomaticContinuations is < 0 or > MaxAutomaticContinuations ||
                    task.ContinuationPending && task.AutomaticContinuations == MaxAutomaticContinuations))
            throw new InvalidDataException("Invalid durable continuation or worker lease; operator reconciliation is required.");
    }

    public static bool HasWork(RepositoryState state, ReconcileOptions? options = null)
    {
        var barrierPending = state.Issues.Values.Any(issue => issue.ContinuationHoldBarrier is not null);
        return state.Outbox.Values.Any(effect => effect.Status == DeliveryStatus.Executing ||
            effect.Status == DeliveryStatus.Pending && (options?.AllowsEffect(effect) ?? true) &&
            (!barrierPending || effect.Kind is not (EffectKind.LaunchTask or EffectKind.RunCi or EffectKind.RequestReview))) ||
            options?.ProjectionOnly != true && !barrierPending && (
            state.Issues.Values.Any(issue =>
                !issue.Paused && !issue.ReactivationRequired && (issue.Ready || issue.ActiveAuthorized) &&
                (issue.Phase is Phase.READY or Phase.FIX_PENDING &&
                    (options?.AllowedEffectKinds?.Contains(EffectKind.LaunchTask) ?? true) ||
                 issue.Phase is Phase.CODING or Phase.CI_PENDING ||
                 issue.Phase == Phase.REVIEW_PENDING &&
                    (options?.AllowedEffectKinds?.Contains(EffectKind.RequestReview) ?? true))) ||
            state.Issues.Values.Any(issue => issue.Ci is { RunId: not null, Green: null, TerminalStopReason: null }));
    }

    public static bool RequiresObservation(RepositoryState state) =>
        state.Issues.Values.Any(issue => issue.Phase != Phase.CLOSED &&
            (issue.PullRequest is not null || issue.ActiveTaskId is not null));

    public static RepositoryState Prepare(RepositoryState input, DateTimeOffset now, string reason, bool inFlight = false,
        ReconcileOptions? options = null)
    {
        var state = StateJson.Clone(input);
        if (inFlight || HasWork(state, options))
        {
            var id = state.Continuation is { Pending: true } existing &&
                existing.RepositoryGeneration == state.Generation ? existing.Id :
                $"continue:{state.Generation}:{state.Version + 1}";
            state.Continuation = new(id, state.Generation, now + TimeSpan.FromMinutes(5), reason);
        }
        else if (state.Continuation is { Pending: true } settled)
            state.Continuation = settled with { Pending = false, Reason = reason };
        state.Version++;
        return state;
    }

    public static bool IsDue(RepositoryState state, DateTimeOffset now)
    {
        Validate(state);
        return state.Continuation is { Pending: true } ticket && ticket.DueAt <= now;
    }
}
