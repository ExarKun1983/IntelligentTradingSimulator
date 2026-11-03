namespace IntelligentTradingSimulator.Orchestration;

public sealed record ContinuationTicket(string Id, long RepositoryGeneration, DateTimeOffset DueAt,
    string Reason, bool Pending = true);

/// <summary>A scheduled wake consumes canonical work, not a synthetic lifecycle command.</summary>
public static class OrchestrationContinuation
{
    public static void Validate(RepositoryState state)
    {
        if (state.Continuation is { } ticket && (string.IsNullOrWhiteSpace(ticket.Id) || ticket.Id.Length > 256 ||
                ticket.RepositoryGeneration != state.Generation || ticket.DueAt == default ||
                string.IsNullOrWhiteSpace(ticket.Reason) || ticket.Reason.Length > 2048) ||
            state.WorkerLease is { } lease && (string.IsNullOrWhiteSpace(lease.Owner) || lease.Owner.Length > 256 ||
                lease.ExpiresAt == default))
            throw new InvalidDataException("Invalid durable continuation or worker lease; operator reconciliation is required.");
    }

    public static bool HasWork(RepositoryState state) =>
        state.Outbox.Values.Any(effect => effect.Status is DeliveryStatus.Pending or DeliveryStatus.Executing) ||
        state.Issues.Values.Any(issue =>
            !issue.Paused && !issue.ReactivationRequired && (issue.Ready || issue.ActiveAuthorized) &&
            issue.Phase is Phase.READY or Phase.CODING or Phase.CI_PENDING or Phase.REVIEW_PENDING or Phase.FIX_PENDING) ||
        state.Issues.Values.Any(issue => issue.Ci is { RunId: not null, Green: null, TerminalStopReason: null });

    public static bool RequiresObservation(RepositoryState state) =>
        state.Issues.Values.Any(issue => issue.Phase != Phase.CLOSED &&
            (issue.PullRequest is not null || issue.ActiveTaskId is not null));

    public static RepositoryState Prepare(RepositoryState input, DateTimeOffset now, string reason, bool inFlight = false)
    {
        var state = StateJson.Clone(input);
        if (inFlight || HasWork(state))
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
