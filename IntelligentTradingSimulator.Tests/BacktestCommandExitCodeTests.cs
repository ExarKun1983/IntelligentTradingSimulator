using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestCommandExitCodeTests
{
    [Theory]
    [InlineData(BacktestRunStatus.Completed, 0)]
    [InlineData(BacktestRunStatus.Failed, 1)]
    public async Task RunBacktestCommand_ReturnsExitCodeForEngineResult(
        BacktestRunStatus status, int expectedExitCode)
    {
        var engine = new StubBacktestEngine(runId => status);
        var reports = new StubReportService();

        var exitCode = await RunCommandAsync("--run-backtest", engine, reports);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Single(engine.Results);
        Assert.Equal(status == BacktestRunStatus.Completed ? [1] : [], reports.RunIds);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task RunVariantsCommand_ReturnsExitCodeForEntireBatch(bool failFirstVariant, int expectedExitCode)
    {
        var engine = new StubBacktestEngine(runId => failFirstVariant && runId == 1
            ? BacktestRunStatus.Failed
            : BacktestRunStatus.Completed);
        var reports = new StubReportService();

        var exitCode = await RunCommandAsync("--run-variants", engine, reports);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.True(engine.Results.Count >= 2, "The fixture must exercise a multi-variant batch.");
        Assert.Equal(
            Enumerable.Range(failFirstVariant ? 2 : 1, engine.Results.Count - (failFirstVariant ? 1 : 0)),
            reports.RunIds);
    }

    private static Task<int> RunCommandAsync(
        string command, IBacktestEngine engine, IBacktestSummaryReportService reports) =>
        TradingSimulatorCommandLine.RunAsync(
            [command, "--backtest-start=2025-01-02", "--backtest-end=2025-01-03", "--no-progress"],
            services =>
            {
                services.AddSingleton(engine);
                services.AddSingleton(reports);
            });

    private sealed class StubBacktestEngine(Func<int, BacktestRunStatus> statusForRun) : IBacktestEngine
    {
        public List<BacktestEngineResult> Results { get; } = [];

        public Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
        {
            var runId = Results.Count + 1;
            var status = statusForRun(runId);
            var result = new BacktestEngineResult
            {
                RunId = runId,
                Status = status,
                ErrorMessage = status == BacktestRunStatus.Failed ? "AI scoring failed (InvalidOperationException)." : null
            };
            Results.Add(result);
            return Task.FromResult(result);
        }
    }

    private sealed class StubReportService : IBacktestSummaryReportService
    {
        public List<int> RunIds { get; } = [];

        public Task<BacktestSummaryReportResult> GenerateAsync(int backtestRunId)
        {
            RunIds.Add(backtestRunId);
            return Task.FromResult(new BacktestSummaryReportResult
            {
                BacktestRunId = backtestRunId,
                IsSuccess = true,
                Status = BacktestRunStatus.Completed
            });
        }
    }
}
