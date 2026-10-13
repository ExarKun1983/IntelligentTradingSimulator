using System;
using System.Collections.Generic;
using System.IO;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

[Collection("Console output")]
public sealed class ConsoleReporterTests
{
    [Fact]
    public void PrintMarketRanking_ShowsFullPrecisionBreakdownAndMissingDiagnostics()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            MomentumScoreWeight = 1m, UseRsi = true, Use52WeekHigh = true
        });
        var score = engine.CalculateScore(new ScoringContext
        {
            MomentumPercent = 1.234567890123456789m,
            VolumeTrendPercent = 20m, VolatilityPercent = 1m, IsAboveSma5 = true,
            Rsi = 60m, DistanceFromWeek52HighPercent = -25m
        }, out _, out var factors);

        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
        [
            new ObservationResult { Symbol = "TEST", Score = score, FactorContributions = factors },
            new ObservationResult { Symbol = "CUSTOM", Score = 42m }
        ]));

        Assert.NotNull(factors);
        Assert.Contains("Score breakdown (points):", output);
        Assert.All(factors, factor => Assert.Contains(
            $"{factor.Factor}={(factor.Contribution.HasValue ? factor.Contribution.Value.ToString("G29") : "N/A")} [{factor.Status}]",
            output));
        Assert.Contains("RelativeStrength=N/A [Unavailable]", output);
        Assert.Contains("Score breakdown: N/A", output);
        Assert.Contains($"Score={score:N2}", output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsDisabledAndDisqualifiedFactorsWithOverride()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration { MomentumScoreWeight = 0m });
        var score = engine.CalculateScore(new ScoringContext
        {
            IsAboveSma5 = false, Rsi = 60m, RelativeStrengthPercent = 3m
        }, out _, out var factors);

        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
            [new ObservationResult { Symbol = "TEST", Score = score, FactorContributions = factors }]));

        Assert.Contains("Momentum=0 [Disabled]", output);
        Assert.Contains("RSI=0 [Disabled]", output);
        Assert.Contains("RelativeStrength=0 [Disqualified]", output);
        Assert.Contains("Disqualification=-999 [Applied]", output);
        Assert.Contains($"Score={StrategyConfiguration.DisqualifiedScore:N2}", output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsAtrOrNA()
    {
        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
        [
            new ObservationResult { Symbol = "AVAILABLE", Atr = 2.345m },
            new ObservationResult { Symbol = "FLAT", Atr = 0m },
            new ObservationResult { Symbol = "MISSING" }
        ]));

        Assert.Contains($"ATR={2.345m.ToString("N2")}", output);
        Assert.Contains($"ATR={0m.ToString("N2")}", output);
        Assert.Contains("ATR=N/A", output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsCompleteFundamentalsWithUnits()
    {
        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
        [
            new ObservationResult
            {
                Symbol = "COMPLETE",
                Fundamentals = new FundamentalData
                {
                    Symbol = "COMPLETE",
                    AsOfDate = new DateOnly(2025, 12, 31),
                    RevenueGrowthPercent = 12.5m,
                    EpsGrowthPercent = 8.25m,
                    Margins = 23.5m,
                    Debt = 1.45m
                }
            }
        ]));

        Assert.Contains(
            $"Fundamentals: AsOf=2025-12-31 | RevGrowth={12.5m:N2}% | EpsGrowth={8.25m:N2}% | Margins={23.5m:N2}% | Debt={1.45m:N2}",
            output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsUnavailablePartialFundamentalsAsNA()
    {
        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
        [
            new ObservationResult
            {
                Symbol = "PARTIAL",
                Fundamentals = new FundamentalData
                {
                    Symbol = "PARTIAL",
                    RevenueGrowthPercent = -5m
                }
            }
        ]));

        Assert.Contains(
            $"Fundamentals: AsOf=N/A | RevGrowth={(-5m):N2}% | EpsGrowth=N/A | Margins=N/A | Debt=N/A",
            output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsAbsentFundamentalsAsNA()
    {
        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
            [new ObservationResult { Symbol = "ABSENT" }]));

        Assert.Contains(
            "Fundamentals: AsOf=N/A | RevGrowth=N/A | EpsGrowth=N/A | Margins=N/A | Debt=N/A",
            output);
    }

    [Fact]
    public void PrintMarketRanking_ShowsRelativeStrengthBenchmarkAndPercentagePointsOrNA()
    {
        var output = CaptureOutput(() => CreateReporter().PrintMarketRanking(
        [
            new ObservationResult
            {
                Symbol = "TEST", RelativeStrengthBenchmarkSymbol = "QQQ",
                RelativeStrengthPercent = -10m,
                RelativeStrengthScoreContribution = -25m
            },
            new ObservationResult { Symbol = "DISABLED", RelativeStrengthScoreContribution = 0m },
            new ObservationResult { Symbol = "MISSING", RelativeStrengthBenchmarkSymbol = "SPY" }
        ]));

        Assert.Contains($"RS[QQQ]={(-10m).ToString("N2")}pp", output);
        Assert.Contains("RS[SPY]=N/A", output);
        Assert.Contains($"RSScore={(-25m).ToString("N2")}", output);
        Assert.Contains($"RSScore={0m.ToString("N2")}", output);
        Assert.Contains("RSScore=N/A", output);
    }

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

    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent, "higher-return", "lowest-drawdown", "highest first")]
    [InlineData(StrategyComparisonSortMetric.MaxDrawdownPercent, "lowest-drawdown", "higher-return", "lowest first")]
    public void PrintVariantComparisonReport_ShowsSortedOrderRanksAndTopStrategy(
        StrategyComparisonSortMetric metric, string first, string second, string direction)
    {
        var report = new StrategyComparisonReportService().Build(
        [
            new BacktestSummaryReportResult
            {
                StrategyId = "lowest-drawdown", IsSuccess = true, ReturnPercent = -5m, MaxDrawdownPercent = 1m
            },
            new BacktestSummaryReportResult
            {
                StrategyId = "failed", IsSuccess = false, ErrorMessage = "Run failed"
            },
            new BacktestSummaryReportResult
            {
                StrategyId = "higher-return", IsSuccess = true, ReturnPercent = 10m, MaxDrawdownPercent = 5m
            }
        ], metric);

        var output = CaptureOutput(() => CreateReporter().PrintVariantComparisonReport(report));

        Assert.Contains($"Sorted by: {metric} ({direction})", output);
        Assert.Contains($"Top-ranked strategy: {first}", output);
        Assert.Contains($"Strategy:       {first}\n  Rank:         1", output.Replace("\r\n", "\n"));
        Assert.Contains($"Strategy:       {second}\n  Rank:         2", output.Replace("\r\n", "\n"));
        Assert.Contains("Strategy:       failed\n  Rank:         N/A", output.Replace("\r\n", "\n"));
        Assert.True(output.IndexOf($"Strategy:       {first}", StringComparison.Ordinal) <
                    output.IndexOf($"Strategy:       {second}", StringComparison.Ordinal));
        Assert.True(output.IndexOf($"Strategy:       {second}", StringComparison.Ordinal) <
                    output.IndexOf("Strategy:       failed", StringComparison.Ordinal));
        Assert.Contains("Run failed", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_BenchmarkSortShowsSymbolAndNoWinnerForMissingMetrics()
    {
        var report = new StrategyComparisonReportService().Build(
            [new BacktestSummaryReportResult { StrategyId = "missing", IsSuccess = true }],
            StrategyComparisonSortMetric.AlphaPercent, "SPY");

        var output = CaptureOutput(() => CreateReporter().PrintVariantComparisonReport(report));

        Assert.Contains("Benchmark: SPY", output);
        Assert.Contains("Top-ranked strategy: N/A", output);
        Assert.Contains("Rank:         N/A", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_UnsortedOutputHasNoRankingLabels()
    {
        var report = new StrategyComparisonReportService().Build(
            [new BacktestSummaryReportResult { StrategyId = "original", IsSuccess = true }]);

        var output = CaptureOutput(() => CreateReporter().PrintVariantComparisonReport(report));

        Assert.Contains("Strategy:       original", output);
        Assert.DoesNotContain("Sorted by:", output);
        Assert.DoesNotContain("Rank:", output);
        Assert.DoesNotContain("Top-ranked strategy:", output);
    }

    [Fact]
    public void PrintMarketRanking_IncludesAnalysisDetailsOrMissingValue()
    {
        var reporter = CreateReporter();

        var output = CaptureOutput(() => reporter.PrintMarketRanking(
        [
            new ObservationResult
            {
                Symbol = "RSI",
                Rsi = 55.5m,
                Week52High = 120m,
                DistanceFromWeek52HighPercent = -10m,
                Score = 12.34m
            },
            new ObservationResult { Symbol = "NONE" }
        ]));

        Assert.Contains("RSI=55.50", output);
        Assert.Contains("RSI=N/A", output);
        Assert.Contains("52WHigh=120.00", output);
        Assert.Contains("52WHigh=N/A", output);
        Assert.Contains("52WDist=-10.00%", output);
        Assert.Contains("52WDist=N/A", output);
        Assert.Contains("Score=12.34", output);
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

    [Fact]
    public void PrintBacktestSummary_IncludesStrategyId_InOutput()
    {
        var reporter = CreateReporter();
        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 5,
            RunName = "Strategy Identity Test",
            StrategyId = "momentum-capped-v2",
            StartCapital = 10_000m,
            FinalCapital = 11_000m,
            Profit = 1_000m,
            ReturnPercent = 10m,
            MaxDrawdownPercent = 3m,
            DrawdownDurationDays = 2,
            TradesCount = 2,
            RebalancesCount = 1
        };

        var output = CaptureOutput(() => reporter.PrintBacktestSummary(result));

        Assert.Contains("momentum-capped-v2", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_IncludesHeader()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "v1",
                    IsSuccess = true,
                    FinalCapital = 110_000m,
                    ReturnPercent = 10m,
                    MaxDrawdownPercent = 5m,
                    TradesCount = 4
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("STRATEGY COMPARISON REPORT", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_IncludesStrategyId()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "momentum-capped-v3",
                    IsSuccess = true,
                    FinalCapital = 105_000m,
                    ReturnPercent = 5m,
                    MaxDrawdownPercent = 2m,
                    TradesCount = 2
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("momentum-capped-v3", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_ShowsFailedStatus_ForFailedEntry()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "broken-variant",
                    IsSuccess = false,
                    ErrorMessage = "Data unavailable"
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("broken-variant", output);
        Assert.Contains("Failed", output);
        Assert.Contains("Data unavailable", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_ShowsReturnAndCapital_ForSuccessfulEntry()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "good-variant",
                    IsSuccess = true,
                    StartCapital = 100_000m,
                    FinalCapital = 120_000m,
                    ReturnPercent = 20m,
                    MaxDrawdownPercent = 6m,
                    TradesCount = 8
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("Start Capital:  100,000.00", output);
        Assert.Contains("120,000.00", output);
        Assert.Contains("20.00%", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_IncludesRiskAndBenchmarkMetrics()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "benchmark-aware",
                    IsSuccess = true,
                    ReturnPercent = 12m,
                    MaxDrawdownPercent = 6m,
                    DrawdownDurationDays = 14,
                    RecoveryTimeDays = null,
                    BenchmarkComparisons =
                    [
                        new BacktestBenchmarkComparison
                        {
                            Symbol = "SPY",
                            StrategyReturnPercent = 12m,
                            BenchmarkReturnPercent = 8m,
                            RelativeReturnPercent = 4m,
                            AlphaPercent = 4m,
                            Outcome = BacktestBenchmarkComparisonOutcome.Outperform
                        }
                    ]
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("Max Drawdown:   6.00%", output);
        Assert.Contains("DD Duration:    14 day(s)", output);
        Assert.Contains("Recovery Time:  N/A (not recovered)", output);
        Assert.Contains("--- Benchmark ---", output);
        Assert.Contains("SPY", output);
        Assert.Contains("Benchmark Return: 8.00%", output);
        Assert.Contains("Relative Return:  4.00%", output);
        Assert.Contains("Alpha:            4.00%", output);
        Assert.Contains("Outperform", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_ShowsNoDrawdown_WhenDurationIsZero()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "no-drawdown",
                    IsSuccess = true,
                    DrawdownDurationDays = 0,
                    RecoveryTimeDays = null
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("Recovery Time:  N/A (no drawdown)", output);
        Assert.DoesNotContain("Recovery Time:  N/A (not recovered)", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_ShowsNotRecovered_WhenDrawdownDurationIsPositive()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "unrecovered-drawdown",
                    IsSuccess = true,
                    DrawdownDurationDays = 1,
                    RecoveryTimeDays = null
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("Recovery Time:  N/A (not recovered)", output);
        Assert.DoesNotContain("Recovery Time:  N/A (no drawdown)", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_EmptyEntries_ShowsNoVariantsMessage()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport { Entries = [] };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("No variant results to compare", output);
    }

    [Fact]
    public void PrintVariantComparisonReport_IncludesAllVariantsInOutput()
    {
        var reporter = CreateReporter();
        var report = new StrategyComparisonReport
        {
            Entries =
            [
                new StrategyComparisonReportEntry
                {
                    StrategyId = "alpha",
                    IsSuccess = true,
                    StartCapital = 100_000m,
                    FinalCapital = 110_000m,
                    ReturnPercent = 10m,
                    MaxDrawdownPercent = 3m,
                    TradesCount = 5
                },
                new StrategyComparisonReportEntry
                {
                    StrategyId = "beta",
                    IsSuccess = false,
                    ErrorMessage = "Data unavailable"
                },
                new StrategyComparisonReportEntry
                {
                    StrategyId = "gamma",
                    IsSuccess = true,
                    StartCapital = 100_000m,
                    FinalCapital = 95_000m,
                    ReturnPercent = -5m,
                    MaxDrawdownPercent = 12m,
                    TradesCount = 2
                }
            ]
        };

        var output = CaptureOutput(() => reporter.PrintVariantComparisonReport(report));

        Assert.Contains("alpha", output);
        Assert.Contains("beta", output);
        Assert.Contains("gamma", output);
        Assert.Contains("Failed", output);
        Assert.Contains("Data unavailable", output);
        Assert.DoesNotContain("Start Capital:  0.00", output);
    }
}
