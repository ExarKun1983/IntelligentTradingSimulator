using System;
using System.Collections.Generic;
using System.IO;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class ConsoleReporterTests
{
    private static ConsoleReporter CreateReporter() =>
        new ConsoleReporter(
            new PortfolioAllocationService(),
            new PortfolioContributionService());

    private static string CaptureOutput(Action action)
    {
        using var writer = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(writer);
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return writer.ToString();
    }

    [Fact]
    public void PrintBacktestSummary_IncludesRiskSectionHeader()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 7,
            RunName = "Issue 15",
            StartCapital = 10_000m,
            FinalCapital = 9_500m,
            Profit = -500m,
            ReturnPercent = -5m,
            MaxDrawdownPercent = 12.34m,
            DrawdownDurationDays = 3,
            TradesCount = 4,
            RebalancesCount = 2
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        Assert.Contains("--- Risk ---", output);
    }

    [Fact]
    public void PrintBacktestSummary_RiskSection_IncludesMaxDrawdown()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 7,
            RunName = "Issue 15",
            StartCapital = 10_000m,
            FinalCapital = 9_500m,
            Profit = -500m,
            ReturnPercent = -5m,
            MaxDrawdownPercent = 12.34m,
            DrawdownDurationDays = 3,
            TradesCount = 4,
            RebalancesCount = 2
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskStart = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        Assert.True(riskStart >= 0, "Risk section header not found");
        Assert.Contains("Max Drawdown:   12.34%", output.Substring(riskStart));
    }

    [Fact]
    public void PrintBacktestSummary_RiskSection_IncludesDrawdownDuration()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "DurationTest",
            StartCapital = 10_000m,
            FinalCapital = 9_000m,
            Profit = -1_000m,
            ReturnPercent = -10m,
            MaxDrawdownPercent = 10m,
            DrawdownDurationDays = 5,
            TradesCount = 1,
            RebalancesCount = 0
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskStart = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        Assert.True(riskStart >= 0, "Risk section header not found");
        Assert.Contains("DD Duration:    5 day(s)", output.Substring(riskStart));
    }

    [Fact]
    public void PrintBacktestSummary_RiskSection_ShowsRecoveryTime_WhenRecovered()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "RecoveryTest",
            StartCapital = 10_000m,
            FinalCapital = 10_500m,
            Profit = 500m,
            ReturnPercent = 5m,
            MaxDrawdownPercent = 8m,
            DrawdownDurationDays = 3,
            RecoveryTimeDays = 4,
            TradesCount = 2,
            RebalancesCount = 1
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskStart = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        Assert.True(riskStart >= 0, "Risk section header not found");
        Assert.Contains("Recovery Time:  4 day(s)", output.Substring(riskStart));
    }

    [Fact]
    public void PrintBacktestSummary_RiskSection_ShowsNotRecovered_WhenNoRecovery()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "NoRecoveryTest",
            StartCapital = 10_000m,
            FinalCapital = 9_000m,
            Profit = -1_000m,
            ReturnPercent = -10m,
            MaxDrawdownPercent = 15m,
            DrawdownDurationDays = 7,
            RecoveryTimeDays = null,
            TradesCount = 1,
            RebalancesCount = 0
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskStart = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        Assert.True(riskStart >= 0, "Risk section header not found");
        Assert.Contains("Recovery Time:  N/A (not recovered)", output.Substring(riskStart));
    }

    [Fact]
    public void PrintBacktestSummary_NoBenchmarkSection_WhenNoBenchmarkComparisons()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "NoBenchmark",
            StartCapital = 10_000m,
            FinalCapital = 10_500m,
            Profit = 500m,
            ReturnPercent = 5m,
            MaxDrawdownPercent = 2m,
            DrawdownDurationDays = 1,
            TradesCount = 1,
            RebalancesCount = 0
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        Assert.DoesNotContain("--- Benchmark ---", output);
    }

    [Fact]
    public void PrintBacktestSummary_IncludesBenchmarkSectionHeader_WhenComparisonsPresent()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "BenchmarkTest",
            StartCapital = 10_000m,
            FinalCapital = 12_000m,
            Profit = 2_000m,
            ReturnPercent = 20m,
            MaxDrawdownPercent = 5m,
            DrawdownDurationDays = 2,
            TradesCount = 3,
            RebalancesCount = 1,
            BenchmarkComparisons =
            [
                new BacktestBenchmarkComparison
                {
                    Symbol = "SPY",
                    StrategyReturnPercent = 20m,
                    BenchmarkReturnPercent = 10m,
                    RelativeReturnPercent = 10m,
                    AlphaPercent = 10m,
                    Outcome = BacktestBenchmarkComparisonOutcome.Outperform
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        Assert.Contains("--- Benchmark ---", output);
    }

    [Fact]
    public void PrintBacktestSummary_BenchmarkSection_IncludesAllComparisonMetrics()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "BenchmarkMetricsTest",
            StartCapital = 10_000m,
            FinalCapital = 12_000m,
            Profit = 2_000m,
            ReturnPercent = 20m,
            MaxDrawdownPercent = 5m,
            DrawdownDurationDays = 2,
            TradesCount = 3,
            RebalancesCount = 1,
            BenchmarkComparisons =
            [
                new BacktestBenchmarkComparison
                {
                    Symbol = "SPY",
                    StrategyReturnPercent = 20m,
                    BenchmarkReturnPercent = 10m,
                    RelativeReturnPercent = 10m,
                    AlphaPercent = 10m,
                    Outcome = BacktestBenchmarkComparisonOutcome.Outperform
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var benchmarkStart = output.IndexOf("--- Benchmark ---", StringComparison.Ordinal);
        Assert.True(benchmarkStart >= 0, "Benchmark section header not found");
        var benchmarkSection = output.Substring(benchmarkStart);

        Assert.Contains("SPY", benchmarkSection);
        Assert.Contains("Strategy Return:  20.00%", benchmarkSection);
        Assert.Contains("Benchmark Return: 10.00%", benchmarkSection);
        Assert.Contains("Relative Return:  10.00%", benchmarkSection);
        Assert.Contains("Alpha:            10.00%", benchmarkSection);
        Assert.Contains("Outcome:          Outperform", benchmarkSection);
    }

    [Fact]
    public void PrintBacktestSummary_BenchmarkMetrics_AppearsAfterRiskSection()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 1,
            RunName = "OrderTest",
            StartCapital = 10_000m,
            FinalCapital = 11_000m,
            Profit = 1_000m,
            ReturnPercent = 10m,
            MaxDrawdownPercent = 3m,
            DrawdownDurationDays = 2,
            TradesCount = 1,
            RebalancesCount = 0,
            BenchmarkComparisons =
            [
                new BacktestBenchmarkComparison
                {
                    Symbol = "QQQ",
                    StrategyReturnPercent = 10m,
                    BenchmarkReturnPercent = 8m,
                    RelativeReturnPercent = 2m,
                    AlphaPercent = 2m,
                    Outcome = BacktestBenchmarkComparisonOutcome.Outperform
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskPos = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        var benchmarkPos = output.IndexOf("--- Benchmark ---", StringComparison.Ordinal);

        Assert.True(riskPos >= 0, "Risk section not found");
        Assert.True(benchmarkPos > riskPos, "Benchmark section should appear after Risk section");
    }

    [Fact]
    public void PrintBacktestSummary_MaxDrawdown_AppearsInRiskSection_NotBeforeIt()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 7,
            RunName = "Issue 15",
            StartCapital = 10_000m,
            FinalCapital = 9_500m,
            Profit = -500m,
            ReturnPercent = -5m,
            MaxDrawdownPercent = 12.34m,
            DrawdownDurationDays = 3,
            TradesCount = 4,
            RebalancesCount = 2
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        var riskPos = output.IndexOf("--- Risk ---", StringComparison.Ordinal);
        var maxDrawdownPos = output.IndexOf("Max Drawdown:", StringComparison.Ordinal);

        Assert.True(riskPos >= 0, "Risk section header not found");
        Assert.True(maxDrawdownPos > riskPos, "Max Drawdown should appear inside the Risk section");
    }
}
