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

        await fixture.Workflow.RunAsync(runId, null, fixture.SnapshotPath);

        var csv = await File.ReadAllTextAsync(fixture.SnapshotPath);
        Assert.Contains($"2024-01-02,{runId},", csv);
        Assert.DoesNotContain($"2024-01-02,{85 - runId},", csv);
        Assert.Equal(1, fixture.Runs.ExistsCalls);
        Assert.Equal(0, fixture.Runs.GetByIdCalls);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
    }

    [Theory]
    [InlineData(44, BacktestRunStatus.Running)]
    [InlineData(45, BacktestRunStatus.Failed)]
    public async Task RunAsync_ExportsPartialSnapshotsForNonCompletedRuns(
        int runId, BacktestRunStatus status)
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();

        await fixture.Workflow.RunAsync(runId, null, fixture.SnapshotPath);

        var csv = await File.ReadAllTextAsync(fixture.SnapshotPath);
        Assert.Contains($"2024-01-02,{runId},", csv);
        Assert.Equal(status, (await fixture.Runs.GetByIdAsync(runId))!.Status);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
    }

    [Fact]
    public async Task RunAsync_UnknownSnapshotRunPreservesFileWithoutSummaryOrBenchmarks()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        await File.WriteAllTextAsync(fixture.SnapshotPath, "existing output");

        await fixture.Workflow.RunAsync(99, null, fixture.SnapshotPath);

        Assert.Equal("existing output", await File.ReadAllTextAsync(fixture.SnapshotPath));
        Assert.Equal(1, fixture.Runs.ExistsCalls);
        Assert.Equal(0, fixture.Runs.GetByIdCalls);
        Assert.Equal(0, fixture.Summary.Calls);
        Assert.Equal(0, fixture.Benchmark.Calls);
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
        private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        private readonly TradingSimulatorDbContext _db = new(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public string SnapshotPath => Path.Combine(_directory, "snapshots.csv");
        public string SummaryPath => Path.Combine(_directory, "summary.csv");
        public CountingRunRepository Runs { get; }
        public CountingBenchmarkService Benchmark { get; } = new();
        public CountingSummaryService Summary { get; }
        public BacktestReportWorkflow Workflow { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Runs = new CountingRunRepository(new BacktestRunRepository(_db));
            var snapshots = new BacktestSnapshotRepository(_db);
            Summary = new CountingSummaryService(new BacktestSummaryReportService(Runs, snapshots, Benchmark));
            Workflow = new BacktestReportWorkflow(Runs, snapshots, new BacktestSnapshotCsvExporter(),
                Summary, new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
                new BacktestSummaryCsvExporter());
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

            public Task AddAsync(BacktestRun run) => inner.AddAsync(run);

            public Task<bool> ExistsAsync(int id)
            {
                ExistsCalls++;
                return inner.ExistsAsync(id);
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

        public Task<BacktestSummaryReportResult> GenerateAsync(int backtestRunId)
        {
            Calls++;
            return inner.GenerateAsync(backtestRunId);
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
