using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyComparisonCommandLineParserTests
{
    [Theory]
    [InlineData("ReturnPercent", StrategyComparisonSortMetric.ReturnPercent)]
    [InlineData("MaxDrawdownPercent", StrategyComparisonSortMetric.MaxDrawdownPercent)]
    [InlineData("TradesCount", StrategyComparisonSortMetric.TradesCount)]
    [InlineData("AlphaPercent", StrategyComparisonSortMetric.AlphaPercent)]
    [InlineData("RelativeReturnPercent", StrategyComparisonSortMetric.RelativeReturnPercent)]
    [InlineData("rEtUrNpErCeNt", StrategyComparisonSortMetric.ReturnPercent)]
    [InlineData("mAxDrAwDoWnPeRcEnT", StrategyComparisonSortMetric.MaxDrawdownPercent)]
    [InlineData("tRaDeScOuNt", StrategyComparisonSortMetric.TradesCount)]
    [InlineData("aLpHaPeRcEnT", StrategyComparisonSortMetric.AlphaPercent)]
    [InlineData("rElAtIvErEtUrNpErCeNt", StrategyComparisonSortMetric.RelativeReturnPercent)]
    public void TryParse_RecognizesMetricNamesAndCaseInsensitiveOptions(
        string metricName, StrategyComparisonSortMetric expected)
    {
        var success = StrategyComparisonCommandLineParser.TryParse(
            [$"--COMPARISON-SORT-BY={metricName}", "--COMPARISON-BENCHMARK= spy "],
            out var sortBy, out var benchmark, out var error);

        Assert.True(success);
        Assert.Equal(expected, sortBy);
        Assert.Equal("spy", benchmark);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("3")]
    [InlineData("4")]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("ReturnPercent, TradesCount")]
    [InlineData(" ReturnPercent ")]
    public void TryParse_RejectsInvalidSortValues(string metricName)
    {
        var success = StrategyComparisonCommandLineParser.TryParse(
            [$"--comparison-sort-by={metricName}"],
            out var sortBy, out _, out var error);

        Assert.False(success);
        Assert.Null(sortBy);
        Assert.Equal(
            "Error: --comparison-sort-by must be ReturnPercent, MaxDrawdownPercent, TradesCount, AlphaPercent, or RelativeReturnPercent.",
            error);
    }

    [Theory]
    [InlineData("AlphaPercent", null)]
    [InlineData("AlphaPercent", "")]
    [InlineData("AlphaPercent", " \t ")]
    [InlineData("RelativeReturnPercent", null)]
    [InlineData("RelativeReturnPercent", "")]
    [InlineData("RelativeReturnPercent", " \t ")]
    public void TryParse_RequiresNonblankBenchmarkForRelativeMetrics(string metricName, string? symbol)
    {
        string[] args = symbol is null
            ? [$"--comparison-sort-by={metricName}"]
            : [$"--comparison-sort-by={metricName}", $"--comparison-benchmark={symbol}"];

        var success = StrategyComparisonCommandLineParser.TryParse(
            args, out _, out _, out var error);

        Assert.False(success);
        Assert.Equal("Error: benchmark sorting requires --comparison-benchmark=<symbol>.", error);
    }

    [Theory]
    [InlineData("ReturnPercent")]
    [InlineData("MaxDrawdownPercent")]
    [InlineData("TradesCount")]
    public void TryParse_DoesNotRequireBenchmarkForAbsoluteMetrics(string metricName)
    {
        var success = StrategyComparisonCommandLineParser.TryParse(
            [$"--comparison-sort-by={metricName}"], out var sortBy, out var benchmark, out var error);

        Assert.True(success);
        Assert.NotNull(sortBy);
        Assert.Null(benchmark);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_OmittedOptionsPreserveUnsortedDefaults()
    {
        var success = StrategyComparisonCommandLineParser.TryParse(
            ["--run-variants", "--backtest-start=2025-01-01"],
            out var sortBy, out var benchmark, out var error);

        Assert.True(success);
        Assert.Null(sortBy);
        Assert.Null(benchmark);
        Assert.Null(error);

        var report = new StrategyComparisonReportService().Build(
            [
                new BacktestSummaryReportResult { IsSuccess = true, StrategyId = "z", ReturnPercent = 1m },
                new BacktestSummaryReportResult { IsSuccess = true, StrategyId = "a", ReturnPercent = 10m }
            ],
            sortBy, benchmark);

        Assert.Equal(["z", "a"], report.Entries.Select(entry => entry.StrategyId));
        Assert.All(report.Entries, entry => Assert.Null(entry.Rank));
        Assert.Null(report.SortBy);
    }

    [Fact]
    public void TryParse_BenchmarkWithoutSortDoesNotEnableSorting()
    {
        var success = StrategyComparisonCommandLineParser.TryParse(
            ["--comparison-benchmark= QQQ "], out var sortBy, out var benchmark, out var error);

        Assert.True(success);
        Assert.Null(sortBy);
        Assert.Equal("QQQ", benchmark);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("aLpHaPeRcEnT", StrategyComparisonSortMetric.AlphaPercent, "alpha-winner")]
    [InlineData("rElAtIvErEtUrNpErCeNt", StrategyComparisonSortMetric.RelativeReturnPercent, "relative-winner")]
    public void TryParse_SelectedOptionsArePassedToComparisonReport(
        string metricName, StrategyComparisonSortMetric expectedMetric, string expectedTopStrategy)
    {
        Assert.True(StrategyComparisonCommandLineParser.TryParse(
            [$"--comparison-sort-by={metricName}", "--comparison-benchmark= qqq "],
            out var sortBy, out var benchmark, out var error));
        Assert.Null(error);

        var summaries = new[]
        {
            new BacktestSummaryReportResult
            {
                IsSuccess = true,
                StrategyId = "alpha-winner",
                BenchmarkComparisons =
                [
                    new BacktestBenchmarkComparison { Symbol = "SPY", AlphaPercent = 0m, RelativeReturnPercent = 0m },
                    new BacktestBenchmarkComparison { Symbol = "QQQ", AlphaPercent = 10m, RelativeReturnPercent = 1m }
                ]
            },
            new BacktestSummaryReportResult
            {
                IsSuccess = true,
                StrategyId = "relative-winner",
                BenchmarkComparisons =
                [
                    new BacktestBenchmarkComparison { Symbol = "SPY", AlphaPercent = 20m, RelativeReturnPercent = 20m },
                    new BacktestBenchmarkComparison { Symbol = "QQQ", AlphaPercent = 1m, RelativeReturnPercent = 10m }
                ]
            }
        };

        var report = new StrategyComparisonReportService().Build(summaries, sortBy, benchmark);

        Assert.Equal(expectedMetric, report.SortBy);
        Assert.Equal("qqq", report.BenchmarkSymbol);
        Assert.Equal(expectedTopStrategy, report.TopStrategy?.StrategyId);
        Assert.Equal(expectedTopStrategy, report.Entries[0].StrategyId);
        Assert.Equal(1, report.Entries[0].Rank);
    }
}
