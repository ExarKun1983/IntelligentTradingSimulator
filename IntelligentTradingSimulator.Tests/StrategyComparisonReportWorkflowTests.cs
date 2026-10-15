using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyComparisonReportWorkflowTests
{
    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent, null)]
    [InlineData(StrategyComparisonSortMetric.AlphaPercent, "SPY")]
    public async Task RunAsync_ExportsBuiltReportWithRanksBenchmarksAndFailures(
        StrategyComparisonSortMetric sortBy, string? benchmark)
    {
        var path = Path.Combine(Path.GetTempPath(), $"comparison-{Guid.NewGuid():N}.csv");
        try
        {
            var workflow = new StrategyComparisonReportWorkflow(
                new StrategyComparisonReportService(),
                new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
                new StrategyComparisonCsvExporter());
            await workflow.RunAsync(
            [
                new() { StrategyId = "lower", IsSuccess = true,
                    Status = IntelligentTradingSimulator.Core.Enums.BacktestRunStatus.Completed, ReturnPercent = -2m,
                    BenchmarkComparisons = [new() { Symbol = "SPY", AlphaPercent = -3m }] },
                new() { StrategyId = "failed", ErrorMessage = "No data" },
                new() { StrategyId = "higher", IsSuccess = true,
                    Status = IntelligentTradingSimulator.Core.Enums.BacktestRunStatus.Completed, ReturnPercent = 10m,
                    BenchmarkComparisons = [new() { Symbol = "SPY", AlphaPercent = 9m }] }
            ], sortBy, benchmark, path);

            var lines = await File.ReadAllLinesAsync(path);
            Assert.Equal(4, lines.Length);
            Assert.Contains("SPY.AlphaPercent", lines[0]);
            Assert.StartsWith("higher,1,True,", lines[1]);
            Assert.StartsWith("lower,2,True,", lines[2]);
            Assert.StartsWith("failed,,False,No data,", lines[3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_WithoutExportStillBuildsAndPrintsReport()
    {
        var workflow = new StrategyComparisonReportWorkflow(
            new StrategyComparisonReportService(),
            new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
            new StrategyComparisonCsvExporter());

        await workflow.RunAsync([new() { StrategyId = "default", IsSuccess = true }], null, null, null);
    }
}
