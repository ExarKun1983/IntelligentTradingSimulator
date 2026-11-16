namespace IntelligentTradingSimulator.Orchestration;

/// <summary>Continues only an admitted task, with a durable per-task limit.</summary>
public sealed class AutomaticWorkerContinuation(IStateStore store, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan LeaseWaitLimit = TimeSpan.FromMinutes(150);

    public async Task<WorkerResult> RunAsync(Func<CancellationToken, Task<WorkerResult>> run,
        CancellationToken token = default, Func<CancellationToken, Task<WorkerResult>>? publishHold = null)
    {
        var waitStarted = clock.GetTimestamp();
        while (true)
        {
            var before = (await store.LoadAsync(token)).State.AdmittedIssue;
            var result = await run(token);
            if (result.Reason == WorkerStopReason.LeaseHeld)
            {
                if (clock.GetElapsedTime(waitStarted) >= LeaseWaitLimit)
                    throw new InvalidOperationException("Worker lease remained occupied; this wake needs operator attention.");
                await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
                continue;
            }
            waitStarted = clock.GetTimestamp();
            if (result.Reason == WorkerStopReason.ContinuationLimit)
            {
                if (publishHold is not null) await publishHold(token);
                return result;
            }
            if (result.Reason is not (WorkerStopReason.TimeLimit or WorkerStopReason.IterationLimit))
                return result;

            var state = (await store.LoadAsync(token)).State;
            OrchestrationContinuation.Validate(state);
            var affected = result.LastReconciliation?.State.AdmittedIssue ?? before ?? state.AdmittedIssue;
            if (affected is { } number && state.Issues.TryGetValue(number, out var issue) &&
                issue.HoldReason == OrchestrationReducer.ContinuationLimitReason && issue.ReactivationRequired)
            {
                if (publishHold is not null) await publishHold(token);
                return result with { Reason = WorkerStopReason.ContinuationLimit };
            }
            if (OrchestrationWorker.ActiveTask(state)?.ContinuationPending != true &&
                state.WorkerLease is null)
                return result;
        }
    }
}
