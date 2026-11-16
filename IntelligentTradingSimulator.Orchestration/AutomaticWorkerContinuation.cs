namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Continues only an admitted task, with a durable per-task limit.</summary>
public sealed class AutomaticWorkerContinuation(IStateStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<WorkerResult> RunAsync(Func<CancellationToken, Task<WorkerResult>> run,
        CancellationToken token = default, Func<CancellationToken, Task<WorkerResult>>? publishHold = null)
    {
        var waitStarted = clock.GetTimestamp();
        var retries = 0;
        var limitReached = false;
        while (true)
        {
            var result = await run(token);
            if (result.Reason == WorkerStopReason.LeaseHeld)
            {
                var elapsed = clock.GetElapsedTime(waitStarted);
                if (elapsed >= LeaseWait.Limit)
                    throw new InvalidOperationException("Worker lease remained occupied; this wake needs operator attention.");
                await Task.Delay(LeaseWait.NextDelay(retries++, LeaseWait.Limit - elapsed), clock, token);
                continue;
            }
            waitStarted = clock.GetTimestamp();
            retries = 0;
            if (result.Reason == WorkerStopReason.ContinuationLimit)
            {
                limitReached = true;
                if (await PublishAndHasQueuedWorkAsync(publishHold, token)) continue;
                return result;
            }
            if (result.Reason is not (WorkerStopReason.TimeLimit or WorkerStopReason.IterationLimit))
                return limitReached ? result with { Reason = WorkerStopReason.ContinuationLimit } : result;

            var state = (await store.LoadAsync(token)).State;
            OrchestrationContinuation.Validate(state);
            var affected = result.LastReconciliation?.State.AdmittedIssue ?? result.LeaseAdmittedIssue ?? state.AdmittedIssue;
            if (affected is { } number && state.Issues.TryGetValue(number, out var issue) &&
                issue.HoldReason == OrchestrationReducer.ContinuationLimitReason && issue.ReactivationRequired)
            {
                limitReached = true;
                if (await PublishAndHasQueuedWorkAsync(publishHold, token)) continue;
                return result with { Reason = WorkerStopReason.ContinuationLimit };
            }

            if (OrchestrationWorker.ActiveTask(state)?.ContinuationPending != true &&
                state.WorkerLease is null)
                return limitReached ? result with { Reason = WorkerStopReason.ContinuationLimit } : result;
        }
    }

    private async Task<bool> PublishAndHasQueuedWorkAsync(
        Func<CancellationToken, Task<WorkerResult>>? publishHold, CancellationToken token)
    {
        if (publishHold is null) return false;
        await publishHold(token);
        var state = (await store.LoadAsync(token)).State;
        return state.AdmittedIssue is null && state.Issues.Values.Any(issue =>
            (issue.Ready || issue.ActiveAuthorized) && !issue.Paused && !issue.ReactivationRequired &&
            issue.Phase is Phase.READY or Phase.FIX_PENDING);
    }
}

internal static class LeaseWait
{
    internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(150);

    internal static TimeSpan NextDelay(int retries, TimeSpan remaining)
    {
        var seconds = Math.Min(120, 5 * Math.Pow(2, Math.Min(retries, 5)));
        var jittered = TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.2));
        return jittered < remaining ? jittered : remaining;
    }
}
