using System;
using System.Collections.Generic;
using System.Linq;
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
            Status = IntelligentTradingSimulator.Core.Enums.BacktestRunStatus.Completed,
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
    public void Build_PreservesEachVariantsDatesIncludingFailedRuns()
    {
        var summaries = new[]
        {
            BuildSuccess("default"),
            BuildSuccess("conservative"),
            BuildFailed("aggressive")
        };
        var lookbacks = new[] { 90, 180, 60 };
        for (var index = 0; index < summaries.Length; index++)
        {
            summaries[index].StartDate = new DateTime(2020, 1, 1);
            summaries[index].EndDate = new DateTime(2024, 12, 31);
            summaries[index].HistoricalStartDate = summaries[index].StartDate.AddDays(-lookbacks[index]);
        }

        var report = CreateService().Build(summaries, StrategyComparisonSortMetric.ReturnPercent);

        foreach (var entry in report.Entries)
        {
            var summary = summaries.Single(s => s.StrategyId == entry.StrategyId);
            Assert.Equal(summary.StartDate, entry.StartDate);
            Assert.Equal(summary.EndDate, entry.EndDate);
            Assert.Equal(summary.HistoricalStartDate, entry.HistoricalStartDate);
        }
    }

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

    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent, "high", "middle", "low")]
    [InlineData(StrategyComparisonSortMetric.MaxDrawdownPercent, "low", "middle", "high")]
    [InlineData(StrategyComparisonSortMetric.TradesCount, "high", "middle", "low")]
    public void Build_SortsAndRanksBySelectedMetric(
        StrategyComparisonSortMetric metric, string first, string second, string third)
    {
        var summaries = new[]
        {
            BuildSuccess("middle", returnPercent: 0m, maxDrawdownPercent: 5m, tradesCount: 3),
            BuildSuccess("low", returnPercent: -10m, maxDrawdownPercent: 0m, tradesCount: 0),
            BuildSuccess("high", returnPercent: 20m, maxDrawdownPercent: 10m, tradesCount: 7)
        };

        var report = CreateService().Build(summaries, metric);

        Assert.Equal(new[] { first, second, third }, report.Entries.Select(entry => entry.StrategyId));
        Assert.Equal(new int?[] { 1, 2, 3 }, report.Entries.Select(entry => entry.Rank));
        Assert.Equal(metric, report.SortBy);
        Assert.Same(report.Entries[0], report.TopStrategy);
        Assert.Equal(new[] { "middle", "low", "high" }, summaries.Select(summary => summary.StrategyId));
        foreach (var entry in report.Entries)
        {
            var original = summaries.Single(summary => summary.StrategyId == entry.StrategyId);
            Assert.Equal(original.ReturnPercent, entry.ReturnPercent);
            Assert.Equal(original.MaxDrawdownPercent, entry.MaxDrawdownPercent);
            Assert.Equal(original.TradesCount, entry.TradesCount);
            Assert.Equal(original.FinalCapital, entry.FinalCapital);
        }
    }

    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent)]
    [InlineData(StrategyComparisonSortMetric.MaxDrawdownPercent)]
    [InlineData(StrategyComparisonSortMetric.TradesCount)]
    public void Build_TiesUseOrdinalStrategyIdRegardlessOfInputOrder(StrategyComparisonSortMetric metric)
    {
        var summaries = new[] { BuildSuccess("beta"), BuildSuccess("alpha"), BuildSuccess("Alpha") };

        var report = CreateService().Build(summaries, metric);
        var reversed = CreateService().Build(summaries.Reverse().ToArray(), metric);

        Assert.Equal(new[] { "Alpha", "alpha", "beta" }, report.Entries.Select(entry => entry.StrategyId));
        Assert.Equal(report.Entries.Select(entry => entry.StrategyId), reversed.Entries.Select(entry => entry.StrategyId));
        Assert.Equal(new int?[] { 1, 2, 3 }, report.Entries.Select(entry => entry.Rank));
    }

    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent)]
    [InlineData(StrategyComparisonSortMetric.MaxDrawdownPercent)]
    [InlineData(StrategyComparisonSortMetric.TradesCount)]
    public void Build_FailedRunsAreLastAndUnranked(StrategyComparisonSortMetric metric)
    {
        var report = CreateService().Build(
            [BuildFailed("z-failed"), BuildSuccess("success", returnPercent: -20m), BuildFailed("a-failed")], metric);

        Assert.Equal(new[] { "success", "a-failed", "z-failed" }, report.Entries.Select(entry => entry.StrategyId));
        Assert.Equal(new int?[] { 1, null, null }, report.Entries.Select(entry => entry.Rank));
        Assert.Equal("success", report.TopStrategy?.StrategyId);
        Assert.Equal("Simulated failure", report.Entries[1].ErrorMessage);
    }

    [Theory]
    [InlineData(StrategyComparisonSortMetric.AlphaPercent, "alpha-winner", "relative-winner")]
    [InlineData(StrategyComparisonSortMetric.RelativeReturnPercent, "relative-winner", "alpha-winner")]
    public void Build_BenchmarkSortingUsesSelectedSymbolAndLeavesMissingValuesUnranked(
        StrategyComparisonSortMetric metric, string first, string second)
    {
        var alphaWinner = BuildSuccess("alpha-winner");
        alphaWinner.BenchmarkComparisons =
        [
            new BacktestBenchmarkComparison { Symbol = "QQQ", AlphaPercent = -100m, RelativeReturnPercent = 100m },
            new BacktestBenchmarkComparison { Symbol = "SPY", AlphaPercent = 5m, RelativeReturnPercent = -5m }
        ];
        var relativeWinner = BuildSuccess("relative-winner");
        relativeWinner.BenchmarkComparisons =
        [
            new BacktestBenchmarkComparison { Symbol = "SPY", AlphaPercent = -5m, RelativeReturnPercent = 5m }
        ];

        var report = CreateService().Build(
            [BuildSuccess("missing"), relativeWinner, BuildFailed("failed"), alphaWinner], metric, "spy");

        Assert.Equal(new[] { first, second, "failed", "missing" }, report.Entries.Select(entry => entry.StrategyId));
        Assert.Equal(new int?[] { 1, 2, null, null }, report.Entries.Select(entry => entry.Rank));
        Assert.Equal("spy", report.BenchmarkSymbol);
        Assert.Equal(first, report.TopStrategy?.StrategyId);
        Assert.Same(alphaWinner.BenchmarkComparisons,
            report.Entries.Single(entry => entry.StrategyId == "alpha-winner").BenchmarkComparisons);
    }

    [Theory]
    [InlineData(StrategyComparisonSortMetric.AlphaPercent)]
    [InlineData(StrategyComparisonSortMetric.RelativeReturnPercent)]
    public void Build_BenchmarkSortingRequiresSymbol(StrategyComparisonSortMetric metric)
    {
        Assert.Throws<ArgumentException>(() => CreateService().Build([], metric));
        Assert.Throws<ArgumentException>(() => CreateService().Build([], metric, " "));
    }

    [Fact]
    public void Build_InvalidSortMetricIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateService().Build([], (StrategyComparisonSortMetric)999));
    }

    [Fact]
    public void Build_NoSortLeavesRanksAndTopStrategyUnset()
    {
        var report = CreateService().Build([BuildSuccess("first"), BuildSuccess("second")]);

        Assert.Null(report.SortBy);
        Assert.Null(report.BenchmarkSymbol);
        Assert.Null(report.TopStrategy);
        Assert.All(report.Entries, entry => Assert.Null(entry.Rank));
    }

    [Fact]
    public void Build_EmptyFailedOrMissingMetricReportsHaveNoTopStrategy()
    {
        var service = CreateService();
        var empty = service.Build([], StrategyComparisonSortMetric.ReturnPercent);
        var failed = service.Build([BuildFailed("failed")], StrategyComparisonSortMetric.ReturnPercent);
        var missing = service.Build([BuildSuccess("missing")], StrategyComparisonSortMetric.AlphaPercent, "SPY");

        Assert.Empty(empty.Entries);
        Assert.Null(empty.TopStrategy);
        Assert.Null(failed.TopStrategy);
        Assert.Null(missing.TopStrategy);
        Assert.Null(Assert.Single(failed.Entries).Rank);
        Assert.Null(Assert.Single(missing.Entries).Rank);
    }
}
