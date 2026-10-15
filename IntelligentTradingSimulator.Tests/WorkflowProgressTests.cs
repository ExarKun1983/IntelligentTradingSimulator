using System.Globalization;
using IntelligentTradingSimulator.App.Simulation;

namespace IntelligentTradingSimulator.Tests;

public sealed class WorkflowProgressTests
{
    [Fact]
    public async Task Heartbeat_ReportsPendingOperationUntilAwaitCompletes()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task FetchAsync()
        {
            using var operation = progress.BeginOperation("Fetching history", "ABC");
            await completion.Task;
        }

        var pending = FetchAsync();
        Assert.False(pending.IsCompleted);
        Assert.Contains("Starting Fetching history", output.ToString());
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains("Still waiting for completion | Operation: Fetching history | Symbol: ABC | Operation elapsed: 5.0s", output.ToString());
        completion.SetResult();
        await pending;
        var closedOutput = output.ToString();
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(closedOutput, output.ToString());
    }

    [Fact]
    public void NestedScopes_RestoreParentOperationSymbolAndElapsedTime()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        using var parent = progress.BeginOperation("Processing date", "PARENT");
        time.Advance(TimeSpan.FromSeconds(2));
        using (progress.BeginOperation("Fetching history", "CHILD"))
        {
            time.Advance(TimeSpan.FromSeconds(8));
            Assert.Contains("Operation: Fetching history | Symbol: CHILD | Operation elapsed: 8.0s", LastLine(output));
        }

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains("Operation: Processing date | Symbol: PARENT | Operation elapsed: 15.0s", LastLine(output));
        Assert.Contains("Workflow elapsed: 15.0s", LastLine(output));
    }

    [Fact]
    public void OutOfOrderAndRepeatedScopeDisposal_DoNotRestoreClosedOperations()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        var parent = progress.BeginOperation("Parent");
        var child = progress.BeginOperation("Child");
        var closedOutput = output.ToString();
        parent.Dispose();
        parent.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(closedOutput, output.ToString());
        using var next = progress.BeginOperation("Next");
        child.Dispose();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains("Operation: Next", LastLine(output));
        next.Dispose();
        closedOutput = output.ToString();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(closedOutput, output.ToString());
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancellation")]
    public void UsingScope_CleansUpWithoutSuccessMessageOnEveryExit(string exit)
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);

        void Execute()
        {
            using var operation = progress.BeginOperation("Loading data");
            if (exit == "failure")
            {
                throw new InvalidOperationException("sensitive error detail");
            }

            if (exit == "cancellation")
            {
                throw new OperationCanceledException();
            }
        }

        var error = Record.Exception(Execute);
        Assert.Equal(exit != "success", error is not null);
        var closedOutput = output.ToString();
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(closedOutput, output.ToString());
        Assert.DoesNotContain("sensitive error detail", closedOutput);
        Assert.DoesNotContain("success", closedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Lines(output));
    }

    [Fact]
    public void ReportsAndRepeatedOperations_AreThrottledButNewKindsAndForcedReportsAreImmediate()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        progress.Report("Starting workflow");
        progress.Report("Routine update");
        using (progress.BeginOperation("Fetching", "FIRST")) { }
        for (var index = 0; index < 100; index++)
        {
            using var operation = progress.BeginOperation("Fetching", "REPEATED");
        }

        using (progress.BeginOperation("Scoring")) { }
        progress.Report("Important update", force: true);
        Assert.Equal(4, Lines(output).Length);
        Assert.DoesNotContain("Routine update", output.ToString());
        Assert.DoesNotContain("REPEATED", output.ToString());
        time.Advance(TimeSpan.FromSeconds(5));
        progress.Report("Next routine update");
        Assert.Equal(5, Lines(output).Length);
        time.Advance(TimeSpan.FromSeconds(5));
        using (progress.BeginOperation("Fetching", "LATER")) { }
        Assert.Contains("Symbol: LATER", LastLine(output));
    }

    [Fact]
    public void Heartbeat_UsesLatestContextAndInvariantCalendarDatesAndPercentages()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        using var operation = progress.BeginOperation("Scoring", "ABC");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            progress.Context = new WorkflowProgressContext
            {
                Workflow = "Backtest",
                StrategyId = "momentum",
                RunId = 42,
                Date = new DateTime(2024, 2, 29, 13, 14, 15),
                CompletedDates = 1,
                TotalDates = 4,
                CompletedSymbols = 2,
                TotalSymbols = 3,
                VariantIndex = 2,
                TotalVariants = 5
            };
            time.Advance(TimeSpan.FromSeconds(5));
            var heartbeat = LastLine(output);
            Assert.Contains("Workflow: Backtest | Strategy: momentum | Run: 42 | Date: 2024-02-29", heartbeat);
            Assert.Contains("Completed calendar dates: 1/4 (25.0%) | Symbols: 2/3 (66.7%) | Variant: 2/5 (40.0%)", heartbeat);
            Assert.Contains("Operation elapsed: 5.0s | Workflow elapsed: 5.0s", heartbeat);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void UnknownOrZeroTotals_DoNotProduceNonFinitePercentages()
    {
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, new ManualTimeProvider());
        progress.Context = new WorkflowProgressContext
        {
            CompletedDates = 0,
            TotalDates = 0,
            CompletedSymbols = 2,
            TotalVariants = 3
        };
        progress.Report("Progress");
        Assert.Contains("Completed calendar dates: 0/0 | Symbols: 2 | Variant: ?/3", output.ToString());
        Assert.DoesNotContain("%", output.ToString());
    }

    [Fact]
    public void Dispose_StopsTimerAndSuppressesEvenQueuedCallbacksAndFurtherReports()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        var progress = new ConsoleWorkflowProgress(output, time);
        var operation = progress.BeginOperation("Pending");
        progress.Dispose();
        progress.Dispose();
        Assert.True(time.Timer!.IsDisposed);
        var disposedOutput = output.ToString();
        time.Timer.FireQueuedCallback();
        time.Advance(TimeSpan.FromSeconds(20));
        progress.Report("Must not appear", force: true);
        using (progress.BeginOperation("Must not appear")) { }
        operation.Dispose();
        Assert.Equal(disposedOutput, output.ToString());
    }

    [Fact]
    public void ClosedScope_SuppressesQueuedHeartbeatWithoutDisposingReporter()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time);
        progress.BeginOperation("Pending").Dispose();
        var closedOutput = output.ToString();
        time.Advance(TimeSpan.FromSeconds(5));
        time.Timer!.FireQueuedCallback();
        Assert.Equal(closedOutput, output.ToString());
        progress.Report("Explicit completion", force: true);
        Assert.Contains("Explicit completion", LastLine(output));
        Assert.DoesNotContain("Operation:", LastLine(output));
    }

    [Fact]
    public void CustomInterval_ControlsHeartbeatAndRoutineReportCadence()
    {
        var time = new ManualTimeProvider();
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, time, TimeSpan.FromSeconds(2));
        using var operation = progress.BeginOperation("Pending");
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(Lines(output));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Lines(output).Length);
        Assert.Contains("Operation elapsed: 2.0s", LastLine(output));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidInterval_IsRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ConsoleWorkflowProgress(new StringWriter(), new ManualTimeProvider(), TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Output_IsOneCompleteLinePerReport()
    {
        using var output = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, new ManualTimeProvider());
        progress.Context = new WorkflowProgressContext { Workflow = "Backtest\r\nworkflow" };
        progress.Report("Working\non data");
        Assert.Single(Lines(output));
        Assert.EndsWith(Environment.NewLine, output.ToString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SynchronousOutputFailure_StopsOutputWithoutInterruptingScopesOrReports(
        bool objectDisposed, bool reportFails)
    {
        var time = new ManualTimeProvider();
        using var output = new FailOnceProgressWriter(
            reportFails ? "Explicit update" : "Starting Loading data", objectDisposed);
        using var progress = new ConsoleWorkflowProgress(output, time);
        using (progress.BeginOperation("Loading data"))
        {
            progress.Report("Explicit update", force: true);
            Assert.True(output.Failed);
            Assert.True(time.Timer!.IsDisposed);
            progress.Context = new WorkflowProgressContext { CompletedSymbols = 1 };
            time.Timer.FireQueuedCallback();
        }

        var closedOutput = output.ToString();
        time.Advance(TimeSpan.FromSeconds(5));
        using (progress.BeginOperation("Loading data")) { }
        progress.Report("Finished", force: true);
        Assert.Equal(1, progress.Context.CompletedSymbols);
        Assert.Equal(closedOutput, output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeartbeatOutputFailure_StopsTimerAndSubsequentOutput(bool writerDisposed)
    {
        var time = new ManualTimeProvider();
        using var output = new HeartbeatFailureWriter(writerDisposed);
        using var progress = new ConsoleWorkflowProgress(output, time);
        using var operation = progress.BeginOperation("Loading data");
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(time.Timer!.IsDisposed);
        Assert.Equal(2, output.WriteAttempts);
        time.Timer.FireQueuedCallback();
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(2, output.WriteAttempts);
        progress.Report("Explicit update", force: true);
        using (progress.BeginOperation("Next operation")) { }
        Assert.Equal(2, output.WriteAttempts);
    }

    [Fact]
    public void NullProgress_IsReusableStatelessAndSilent()
    {
        IWorkflowProgress progress = NullWorkflowProgress.Instance;
        var original = progress.Context;
        progress.Context = new WorkflowProgressContext { Workflow = "Ignored" };
        using (progress.BeginOperation("Ignored", "ABC"))
        {
            progress.Report("Ignored", force: true);
        }

        Assert.Same(progress, NullWorkflowProgress.Instance);
        Assert.Equal(original, progress.Context);
    }

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string LastLine(StringWriter output) => Lines(output)[^1];

    private sealed class HeartbeatFailureWriter(bool writerDisposed) : StringWriter
    {
        public int WriteAttempts { get; private set; }

        public override void WriteLine(string? value)
        {
            WriteAttempts++;
            if (WriteAttempts > 1)
            {
                if (writerDisposed)
                {
                    throw new ObjectDisposedException(nameof(HeartbeatFailureWriter));
                }

                throw new IOException("Output unavailable");
            }

            base.WriteLine(value);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public ManualTimer? Timer { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Null(Timer);
            return Timer = new ManualTimer(this, callback, state, dueTime, period);
        }

        public void Advance(TimeSpan elapsed)
        {
            var target = _timestamp + elapsed.Ticks;
            while (Timer is { IsDisposed: false } timer && timer.Next <= target)
            {
                _timestamp = timer.Next;
                timer.Next += timer.Period.Ticks;
                timer.FireQueuedCallback();
            }

            _timestamp = target;
        }

        public sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;

            public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                Change(dueTime, period);
            }

            public bool IsDisposed { get; private set; }
            public long Next { get; set; }
            public TimeSpan Period { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (IsDisposed)
                {
                    return false;
                }

                Next = _owner.GetTimestamp() + dueTime.Ticks;
                Period = period;
                return true;
            }

            public void FireQueuedCallback() => _callback(_state);
            public void Dispose() => IsDisposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}

internal sealed class FailOnceProgressWriter(string failingMessage, bool objectDisposed) : StringWriter
{
    public bool Failed { get; private set; }

    public override void WriteLine(string? value)
    {
        if (!Failed && value?.Contains(failingMessage, StringComparison.Ordinal) == true)
        {
            Failed = true;
            if (objectDisposed)
            {
                throw new ObjectDisposedException(nameof(FailOnceProgressWriter));
            }

            throw new IOException("Output unavailable");
        }

        base.WriteLine(value);
    }
}
