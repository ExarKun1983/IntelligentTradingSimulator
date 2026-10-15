using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestReportWorkflowTests
{
    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    public async Task RunAsync_SnapshotOnlyExportsRequestedRunWithoutSummaryOrBenchmarks(int runId)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        var succeeded = await fixture.Workflow.RunAsync(runId, null, fixture.SnapshotPath);

        Assert.True(succeeded);
        var csv = await File.ReadAllTextAsync(fixture.SnapshotPath);
        Assert.Contains($"2024-01-02,{runId},", csv);
        Assert.DoesNotContain($"2024-01-02,{85 - runId},", csv);
        Assert.Equal(1, fixture.Runs.ExistsCalls);
        Assert.Equal(0, fixture.Runs.GetByIdCalls);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
        Assert.Contains(fixture.Progress.Events, e => e.Message == "Loading backtest snapshots");
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("generating summary"));
        Assert.Equal("backtest-report", fixture.Progress.Context.Workflow);
        Assert.Equal(runId, fixture.Progress.Context.RunId);
    }

    [Theory]
    [InlineData(44, BacktestRunStatus.Running)]
    [InlineData(45, BacktestRunStatus.Failed)]
    public async Task RunAsync_ExportsPartialSnapshotsForNonCompletedRuns(
        int runId, BacktestRunStatus status)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        var succeeded = await fixture.Workflow.RunAsync(runId, null, fixture.SnapshotPath);

        Assert.True(succeeded);
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("partial data") && e.Force);
        var csv = await File.ReadAllTextAsync(fixture.SnapshotPath);
        Assert.Contains($"2024-01-02,{runId},", csv);
        Assert.Equal(status, (await fixture.Runs.GetByIdAsync(runId))!.Status);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_AnnouncesExportBeforeWritingAndSuccessOnlyAfterWriting(bool snapshots)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var path = snapshots ? fixture.SnapshotPath : fixture.SummaryPath;
        var announced = false;
        fixture.Progress.OnBegin = operation =>
        {
            if (!operation.StartsWith("Exporting"))
                return;
            announced = true;
            Assert.False(File.Exists(path));
            Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));
            Assert.Equal(42, fixture.Progress.Context.RunId);
        };
        fixture.Progress.OnReport = message =>
        {
            if (message.Contains("export complete"))
            {
                Assert.True(announced);
                Assert.True(File.Exists(path));
                Assert.NotEmpty(File.ReadAllText(path));
            }
        };

        await fixture.Workflow.RunAsync(42, snapshots ? null : path, snapshots ? path : null);

        Assert.True(announced);
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("export complete") && e.Force);
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("Command completed"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ExportFailureReportsFailureWithoutSuccess(bool snapshots)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var invalidPath = Path.GetDirectoryName(fixture.SnapshotPath)!;

        var exception = await Record.ExceptionAsync(() => fixture.Workflow.RunAsync(42,
            snapshots ? null : invalidPath, snapshots ? invalidPath : null));

        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Contains(fixture.Progress.Events, e => e.Message.StartsWith("Exporting"));
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("failed") && e.Force);
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));
        Assert.Equal("backtest-report", fixture.Progress.Context.Workflow);
        Assert.Equal(42, fixture.Progress.Context.RunId);
        Assert.Equal(snapshots ? 0 : 1, fixture.Summary.Calls);
        Assert.Equal(snapshots ? 0 : 1, fixture.Benchmark.Calls);
    }

    [Fact]
    public async Task RunAsync_FailedSummaryReportsFailureWithoutExportSuccess()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        var succeeded = await fixture.Workflow.RunAsync(99, fixture.SummaryPath, null);

        Assert.False(succeeded);
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("report failed") && e.Force);
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));
        Assert.False(File.Exists(fixture.SummaryPath));
    }

    [Fact]
    public async Task RunAsync_MissingSnapshotsAndSuccessfulSummaryReturnsFalse()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        fixture.Runs.ExistsResult = false;

        var succeeded = await fixture.Workflow.RunAsync(42, fixture.SummaryPath, fixture.SnapshotPath);

        Assert.False(succeeded);
        Assert.True(File.Exists(fixture.SummaryPath));
        Assert.False(File.Exists(fixture.SnapshotPath));
        Assert.Equal(1, fixture.Summary.Calls);
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("was not found") && e.Force);
    }

    [Fact]
    public async Task RunAsync_PendingSummaryDoesNotAnnounceExportOrCompletion()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var pending = new TaskCompletionSource<BacktestSummaryReportResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Summary.PendingResult = pending.Task;

        var task = fixture.Workflow.RunAsync(42, fixture.SummaryPath, null);
        Assert.False(task.IsCompleted);
        Assert.Contains(fixture.Progress.Events,
            e => e.Message == "Loading report database data and generating summary");
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.StartsWith("Exporting"));
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));

        pending.SetResult(new BacktestSummaryReportResult
        {
            IsSuccess = true,
            Status = BacktestRunStatus.Completed,
            BacktestRunId = 42
        });
        await task;
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("export complete"));
    }

    [Theory]
    [InlineData(44)]
    [InlineData(45)]
    public async Task RunAsync_NonCompletedSummaryExportReportsWarningWithoutSuccess(int runId)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        var succeeded = await fixture.Workflow.RunAsync(runId, fixture.SummaryPath, null);

        Assert.False(succeeded);
        Assert.False(File.Exists(fixture.SummaryPath));
        Assert.Contains(fixture.Progress.Events, e =>
            e.Message == "CSV export requires a completed backtest run." && e.Force);
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));
    }

    [Fact]
    public async Task RunAsync_ReportContextClearsSimulationCountersAndRetainsReportedRunAndStrategy()
    {
        await using var fixture = new Fixture();
        fixture.Progress.Context = new WorkflowProgressContext
        {
            Workflow = "backtest",
            RunId = 7,
            StrategyId = "previous",
            Date = new DateTime(2024, 1, 2),
            CompletedDates = 2,
            TotalDates = 2,
            CompletedSymbols = 3,
            TotalSymbols = 3
        };
        fixture.Summary.PendingResult = Task.FromResult(new BacktestSummaryReportResult
        {
            IsSuccess = true,
            Status = BacktestRunStatus.Completed,
            BacktestRunId = 42,
            StrategyId = "reported-strategy"
        });
        fixture.Progress.OnBegin = _ =>
        {
            Assert.Equal("backtest-report", fixture.Progress.Context.Workflow);
            Assert.Equal(42, fixture.Progress.Context.RunId);
            Assert.Null(fixture.Progress.Context.Date);
            Assert.Null(fixture.Progress.Context.CompletedDates);
            Assert.Null(fixture.Progress.Context.TotalDates);
            Assert.Null(fixture.Progress.Context.CompletedSymbols);
            Assert.Null(fixture.Progress.Context.TotalSymbols);
        };

        Assert.True(await fixture.Workflow.RunAsync(42, null, null));

        Assert.Equal("backtest-report", fixture.Progress.Context.Workflow);
        Assert.Equal(42, fixture.Progress.Context.RunId);
        Assert.Equal("reported-strategy", fixture.Progress.Context.StrategyId);
    }

    [Fact]
    public async Task RunAsync_UnknownSnapshotRunPreservesFileWithoutSummaryOrBenchmarks()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(fixture.SnapshotPath, "existing output");

        var succeeded = await fixture.Workflow.RunAsync(99, null, fixture.SnapshotPath);

        Assert.False(succeeded);
        Assert.Equal("existing output", await File.ReadAllTextAsync(fixture.SnapshotPath));
        Assert.Equal(1, fixture.Runs.ExistsCalls);
        Assert.Equal(0, fixture.Runs.GetByIdCalls);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
        Assert.Contains(fixture.Progress.Events, e => e.Message.Contains("was not found") && e.Force);
        Assert.DoesNotContain(fixture.Progress.Events, e => e.Message.Contains("export complete"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RunAsync_SummaryRequestsStillGenerateSummaryAndFetchBenchmarks(
        bool exportSummary, bool exportSnapshots)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        await fixture.Workflow.RunAsync(42,
            exportSummary ? fixture.SummaryPath : null,
            exportSnapshots ? fixture.SnapshotPath : null);

        Assert.Equal(1, fixture.Summary.Calls);
        Assert.Equal(1, fixture.Benchmark.Calls);
        Assert.Equal(exportSummary, File.Exists(fixture.SummaryPath));
        Assert.Equal(exportSnapshots, File.Exists(fixture.SnapshotPath));
        if (exportSummary)
            Assert.Contains("42", await File.ReadAllTextAsync(fixture.SummaryPath));
        if (exportSnapshots)
            Assert.Contains("2024-01-02,42,", await File.ReadAllTextAsync(fixture.SnapshotPath));
    }

    [Theory]
    [InlineData("--run-backtest")]
    [InlineData("--backtest-run-id=42")]
    public async Task CollidingPaths_AreRejectedBeforeReportingAndLeaveOutputUntouched(string mode)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(fixture.SnapshotPath, "existing output");
        var alias = Path.Combine(Path.GetDirectoryName(fixture.SnapshotPath)!, ".", "snapshots.csv");
        string[] args = [mode, $"--export-backtest-csv={fixture.SnapshotPath}",
            $"--export-backtest-snapshots-csv={alias}"];

        Assert.True(BacktestSummaryCsvCommandLineParser.TryParse(args, out var summaryPath, out _));
        var valid = BacktestSnapshotCsvCommandLineParser.TryParse(args, out var snapshotPath, out var error);
        if (valid)
            await fixture.Workflow.RunAsync(42, summaryPath, snapshotPath);

        Assert.False(valid);
        Assert.Contains("different output paths", error);
        Assert.Equal("existing output", await File.ReadAllTextAsync(fixture.SnapshotPath));
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(),
            $"report-workflow-tests-{Guid.NewGuid():N}");
        private readonly TradingSimulatorDbContext _db = new(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public string SnapshotPath => Path.Combine(_directory, "snapshots.csv");
        public string SummaryPath => Path.Combine(_directory, "summary.csv");
        public CountingRunRepository Runs { get; }
        public CountingBenchmarkService Benchmark { get; } = new();
        public CountingSummaryService Summary { get; }
        public BacktestReportWorkflow Workflow { get; }
        public RecordingProgress Progress { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Runs = new CountingRunRepository(new BacktestRunRepository(_db));
            var snapshots = new BacktestSnapshotRepository(_db);
            Summary = new CountingSummaryService(new BacktestSummaryReportService(Runs, snapshots, Benchmark));
            Workflow = new BacktestReportWorkflow(Runs, snapshots, new BacktestSnapshotCsvExporter(),
                Summary, new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
                new BacktestSummaryCsvExporter(), Progress);
        }

        public sealed class RecordingProgress : IWorkflowProgress
        {
            public WorkflowProgressContext Context { get; set; } = new();
            public List<(string Message, bool Force)> Events { get; } = [];
            public Action<string>? OnBegin { get; set; }
            public Action<string>? OnReport { get; set; }

            public void Report(string message, bool force = false)
            {
                OnReport?.Invoke(message);
                Events.Add((message, force));
            }

            public IDisposable BeginOperation(string operation, string? symbol = null)
            {
                OnBegin?.Invoke(operation);
                Events.Add((operation, false));
                return new Scope();
            }

            private sealed class Scope : IDisposable
            {
                public void Dispose() { }
            }
        }

        public async Task SeedAsync()
        {
            foreach (var (id, status) in new[]
                     {
                         (42, BacktestRunStatus.Completed),
                         (43, BacktestRunStatus.Completed),
                         (44, BacktestRunStatus.Running),
                         (45, BacktestRunStatus.Failed)
                     })
            {
                _db.BacktestRuns.Add(new BacktestRun
                {
                    Id = id,
                    Status = status,
                    Snapshots =
                    [
                        new() { Date = new DateTime(2024, 1, 2), PortfolioValue = id }
                    ]
                });
            }
            await _db.SaveChangesAsync();
        }

        public sealed class CountingRunRepository(IBacktestRunRepository inner) : IBacktestRunRepository
        {
            public int ExistsCalls { get; private set; }
            public int GetByIdCalls { get; private set; }
            public bool? ExistsResult { get; set; }

            public Task AddAsync(BacktestRun run) => inner.AddAsync(run);

            public Task<bool> ExistsAsync(int id)
            {
                ExistsCalls++;
                return ExistsResult.HasValue ? Task.FromResult(ExistsResult.Value) : inner.ExistsAsync(id);
            }

            public Task<BacktestRun?> GetByIdAsync(int id)
            {
                GetByIdCalls++;
                return inner.GetByIdAsync(id);
            }

            public Task UpdateAsync(BacktestRun run) => inner.UpdateAsync(run);

            public Task MarkFailedAsync(int runId) => inner.MarkFailedAsync(runId);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CountingSummaryService(IBacktestSummaryReportService inner) : IBacktestSummaryReportService
    {
        public int Calls { get; private set; }
        public Task<BacktestSummaryReportResult>? PendingResult { get; set; }

        public Task<BacktestSummaryReportResult> GenerateAsync(int backtestRunId)
        {
            Calls++;
            return PendingResult ?? inner.GenerateAsync(backtestRunId);
        }
    }

    private sealed class CountingBenchmarkService : IBacktestBenchmarkService
    {
        public int Calls { get; private set; }

        public Task<BacktestBenchmarkResult> GetForRunAsync(int backtestRunId)
        {
            Calls++;
            return Task.FromResult(new BacktestBenchmarkResult { IsSuccess = true });
        }

        public Task<BacktestBenchmarkResult> GetForRunAsync(int backtestRunId, IEnumerable<string> symbols)
            => GetForRunAsync(backtestRunId);
    }
}
