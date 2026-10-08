using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using Xunit;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyComparisonReportServiceTests
{
    private static StrategyComparisonReportService CreateService() => new StrategyComparisonReportService();

    private static BacktestSummaryReportResult BuildSuccess(
        string strategyId,
        decimal finalCapital = 110_000m,
        decimal returnPercent = 10m,
        decimal maxDrawdownPercent = 5m,
        int tradesCount = 3) =>
        new BacktestSummaryReportResult
        {
            IsSuccess = true,
            StrategyId = strategyId,
            StartCapital = 100_000m,
            FinalCapital = finalCapital,
            ReturnPercent = returnPercent,
            MaxDrawdownPercent = maxDrawdownPercent,
            TradesCount = tradesCount
        };

    private static BacktestSummaryReportResult BuildFailed(string strategyId, string errorMessage = "Simulated failure") =>
        new BacktestSummaryReportResult
        {
            IsSuccess = false,
            StrategyId = strategyId,
            ErrorMessage = errorMessage
        };

    [Fact]
    public void Build_ReturnsOneEntryPerSummary()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildSuccess("alpha"),
            BuildSuccess("beta"),
            BuildSuccess("gamma")
        };

        var report = service.Build(summaries);

        Assert.Equal(3, report.Entries.Count);
    }

    [Fact]
    public void Build_MapsStrategyIdCorrectly()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildSuccess("momentum-v1"),
            BuildSuccess("momentum-v2")
        };

        var report = service.Build(summaries);

        Assert.Equal("momentum-v1", report.Entries[0].StrategyId);
        Assert.Equal("momentum-v2", report.Entries[1].StrategyId);
    }

    [Fact]
    public void Build_MapsFinancialFieldsForSuccessfulEntry()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildSuccess("v1", finalCapital: 115_000m, returnPercent: 15m, maxDrawdownPercent: 8m, tradesCount: 7)
        };

        var report = service.Build(summaries);

        var entry = report.Entries[0];
        Assert.True(entry.IsSuccess);
        Assert.Equal(100_000m, entry.StartCapital);
        Assert.Equal(115_000m, entry.FinalCapital);
        Assert.Equal(15m, entry.ReturnPercent);
        Assert.Equal(8m, entry.MaxDrawdownPercent);
        Assert.Equal(7, entry.TradesCount);
    }

    [Fact]
    public void Build_MapsRiskAndBenchmarkMetricsForSuccessfulEntry()
    {
        var service = CreateService();
        var summary = BuildSuccess("v1");
        summary.DrawdownDurationDays = 12;
        summary.RecoveryTimeDays = 8;
        summary.BenchmarkComparisons =
        [
            new BacktestBenchmarkComparison
            {
                Symbol = "SPY",
                StrategyReturnPercent = 10m,
                BenchmarkReturnPercent = 7m,
                RelativeReturnPercent = 3m,
                AlphaPercent = 3m,
                Outcome = BacktestBenchmarkComparisonOutcome.Outperform
            }
        ];

        var report = service.Build([summary]);

        var entry = Assert.Single(report.Entries);
        Assert.Equal(12, entry.DrawdownDurationDays);
        Assert.Equal(8, entry.RecoveryTimeDays);
        var comparison = Assert.Single(entry.BenchmarkComparisons);
        Assert.Equal("SPY", comparison.Symbol);
        Assert.Equal(7m, comparison.BenchmarkReturnPercent);
        Assert.Equal(3m, comparison.RelativeReturnPercent);
        Assert.Equal(3m, comparison.AlphaPercent);
        Assert.Equal(BacktestBenchmarkComparisonOutcome.Outperform, comparison.Outcome);
    }

    [Fact]
    public void Build_FailedSummary_IsMarkedAsFailure()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildFailed("failing-variant", errorMessage: "Something went wrong")
        };

        var report = service.Build(summaries);

        var entry = report.Entries[0];
        Assert.False(entry.IsSuccess);
        Assert.Equal("failing-variant", entry.StrategyId);
        Assert.Equal("Something went wrong", entry.ErrorMessage);
    }

    [Fact]
    public void Build_MixedSuccessAndFailure_PreservesAllEntries()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildSuccess("ok-variant"),
            BuildFailed("bad-variant"),
            BuildSuccess("also-ok")
        };

        var report = service.Build(summaries);

        Assert.Equal(3, report.Entries.Count);
        Assert.True(report.Entries[0].IsSuccess);
        Assert.False(report.Entries[1].IsSuccess);
        Assert.True(report.Entries[2].IsSuccess);
    }

    [Fact]
    public void Build_EmptyInput_ReturnsEmptyReport()
    {
        var service = CreateService();

        var report = service.Build(new List<BacktestSummaryReportResult>());

        Assert.Empty(report.Entries);
    }

    [Fact]
    public void Build_PreservesOrderOfEntries()
    {
        var service = CreateService();

        var summaries = new List<BacktestSummaryReportResult>
        {
            BuildSuccess("first"),
            BuildSuccess("second"),
            BuildSuccess("third")
        };

        var report = service.Build(summaries);

        Assert.Equal("first", report.Entries[0].StrategyId);
        Assert.Equal("second", report.Entries[1].StrategyId);
        Assert.Equal("third", report.Entries[2].StrategyId);
    }
}
