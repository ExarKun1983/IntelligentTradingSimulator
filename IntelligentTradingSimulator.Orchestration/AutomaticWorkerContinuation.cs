namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Continues only an admitted task, with a durable per-task limit.</summary>
public sealed class AutomaticWorkerContinuation(IStateStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public Task<WorkerResult> RunAsync(Func<CancellationToken, Task<WorkerResult>> run,
        CancellationToken token = default, Func<CancellationToken, Task<WorkerResult>>? publishHold = null)
        => RunCoreAsync(run, token, publishHold is null ? null : (_, cancellationToken) => publishHold(cancellationToken));

    internal Task<WorkerResult> RunForIssueAsync(Func<CancellationToken, Task<WorkerResult>> run,
        Func<int, CancellationToken, Task<WorkerResult>> publishHold, CancellationToken token = default)
        => RunCoreAsync(run, token, publishHold);

    private async Task<WorkerResult> RunCoreAsync(Func<CancellationToken, Task<WorkerResult>> run,
        CancellationToken token, Func<int, CancellationToken, Task<WorkerResult>>? publishHold)
    {
        var limitReached = false;
        while (true)
        {
            var result = await LeaseWait.RunWorkerAsync(run, clock,
                "this wake needs operator attention", token);
            if (result.Reason == WorkerStopReason.ContinuationLimit)
            {
                limitReached = true;
                var heldIssue = result.LastReconciliation?.State.AdmittedIssue ?? result.LeaseAdmittedIssue;
                if (await PublishAndHasQueuedWorkAsync(publishHold, heldIssue, token)) continue;
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
                if (await PublishAndHasQueuedWorkAsync(publishHold, number, token)) continue;
                return result with { Reason = WorkerStopReason.ContinuationLimit };
            }

            if (OrchestrationWorker.ActiveTask(state)?.ContinuationPending != true &&
                state.WorkerLease is null)
                return limitReached ? result with { Reason = WorkerStopReason.ContinuationLimit } : result;
        }
    }

    private async Task<bool> PublishAndHasQueuedWorkAsync(
        Func<int, CancellationToken, Task<WorkerResult>>? publishHold, int? issueNumber, CancellationToken token)
    {
        if (publishHold is null) return false;
        if (issueNumber is not > 0)
            throw new InvalidOperationException("Continuation-limit hold has no identified Issue.");
        await LeaseWait.RunWorkerAsync(cancellationToken => publishHold(issueNumber.Value, cancellationToken), clock,
            "hold publication needs operator attention", token);
        var state = (await store.LoadAsync(token)).State;
        return state.AdmittedIssue is null &&
            !state.Issues.Values.Any(issue => issue.ContinuationHoldBarrier is not null) &&
            state.Issues.Values.Any(issue =>
            (issue.Ready || issue.ActiveAuthorized) && !issue.Paused && !issue.ReactivationRequired &&
            issue.Phase is Phase.READY or Phase.FIX_PENDING);
    }
}

internal static class LeaseWait
{
    internal static readonly TimeSpan Limit = TimeSpan.FromMinutes(150);

    internal static async Task<WorkerResult> RunWorkerAsync(Func<CancellationToken, Task<WorkerResult>> run,
        TimeProvider clock, string timeoutContext, CancellationToken token)
    {
        var started = clock.GetTimestamp();
        var retries = 0;
        while (true)
        {
            var result = await run(token);
            if (result.Reason != WorkerStopReason.LeaseHeld) return result;
            var elapsed = clock.GetElapsedTime(started);
            if (elapsed >= Limit)
                throw new InvalidOperationException($"Worker lease remained occupied for {elapsed:c} (limit {Limit:c}); {timeoutContext}.");
            await Task.Delay(NextDelay(retries++, Limit - elapsed), clock, token);
        }
    }

    internal static TimeSpan NextDelay(int retries, TimeSpan remaining)
    {
        var seconds = Math.Min(120, 5 * Math.Pow(2, Math.Min(retries, 5)));
        var jittered = TimeSpan.FromSeconds(seconds * (0.8 + Random.Shared.NextDouble() * 0.2));
        return jittered < remaining ? jittered : remaining;
    }
}
