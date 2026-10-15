using System.Globalization;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class ResultExplanationServiceTests
{
    private readonly ResultExplanationService _service = new();

    [Theory]
    [InlineData(12.5, 5, 2, 3)]
    [InlineData(-12.5, 20, 0, null)]
    [InlineData(0, 0, 0, null)]
    public void Explain_Backtest_UsesActualMetrics(decimal returns, decimal drawdown, int duration, int? recovery)
    {
        var report = Completed();
        report.ReturnPercent = returns;
        report.MaxDrawdownPercent = drawdown;
        report.DrawdownDurationDays = duration;
        report.RecoveryTimeDays = recovery;

        var result = _service.Explain(report);
        var text = string.Join(" ", result.Details);

        Assert.Contains($"returned {returns.ToString(CultureInfo.InvariantCulture)}%", result.Summary);
        Assert.Contains("2024-01-01 to 2024-01-31 (run 42)", result.Summary);
        Assert.Contains("Capital moved from 100 to 112.5; reported profit was 12.5.", text);
        Assert.Contains($"Maximum drawdown was {drawdown}%", text);
        Assert.Contains($"{duration} calendar day(s) from peak to trough", text);
        Assert.Contains("4 trades were executed", text);
        Assert.Contains("2 rebalances were recorded", text);
        Assert.Contains(drawdown == 0 ? "recovery time is not applicable"
            : recovery.HasValue ? $"took {recovery} calendar day(s)" : "had not recovered", text);
        Assert.Contains("Benchmark metrics are unavailable", text);
        Assert.Contains(result.Limitations, item => item.Contains("causal drivers and factor differences cannot be determined"));
    }

    [Theory]
    [InlineData(10, "outperformed")]
    [InlineData(0, "matched")]
    [InlineData(-10, "underperformed")]
    public void Explain_Benchmark_DescribesActualReturnDifference(decimal difference, string outcome)
    {
        var report = Completed();
        report.BenchmarkComparisons = [Benchmark(difference)];

        var text = string.Join(" ", _service.Explain(report).Details);

        Assert.Contains("Against SPY", text);
        Assert.Contains($"{outcome} benchmark return 5%", text);
        Assert.Contains($"relative return was {difference} percentage points", text);
        Assert.Contains($"alpha was {difference} percentage points", text);
        Assert.Contains("not risk-adjusted", text);
    }

    [Theory]
    [InlineData(BacktestRunStatus.Running)]
    [InlineData(BacktestRunStatus.Failed)]
    public void Explain_IncompleteBacktest_DoesNotExplainOutcome(BacktestRunStatus status)
    {
        var report = Completed();
        report.Status = status;
        var result = _service.Explain(report);
        Assert.Contains($"is {status}", result.Summary);
        Assert.Empty(result.Details);
    }

    [Fact]
    public void Explain_FailedSummary_UsesErrorNotDefaultMetrics()
    {
        var result = _service.Explain(new BacktestSummaryReportResult { ErrorMessage = "No snapshots." });
        Assert.Equal("Backtest summary unavailable.", result.Summary);
        Assert.Contains("No snapshots.", result.Limitations);
        Assert.Empty(result.Details);
    }

    [Theory]
    [InlineData(StrategyComparisonSortMetric.ReturnPercent, "A", "10")]
    [InlineData(StrategyComparisonSortMetric.MaxDrawdownPercent, "B", "2")]
    [InlineData(StrategyComparisonSortMetric.TradesCount, "B", "8")]
    [InlineData(StrategyComparisonSortMetric.AlphaPercent, "B", "3")]
    [InlineData(StrategyComparisonSortMetric.RelativeReturnPercent, "B", "3")]
    public void Explain_Comparison_RespectsSelectedMetricAndReportsRiskTradeoff(
        StrategyComparisonSortMetric metric, string leader, string value)
    {
        var a = Completed();
        a.StrategyId = "A";
        a.ReturnPercent = 10;
        a.MaxDrawdownPercent = 20;
        a.BenchmarkComparisons = [Benchmark(1)];
        var b = Completed();
        b.StrategyId = "B";
        b.ReturnPercent = 8;
        b.MaxDrawdownPercent = 2;
        b.TradesCount = 8;
        b.BenchmarkComparisons = [Benchmark(3)];
        var report = new StrategyComparisonReportService().Build([a, b], metric, "SPY");

        var result = _service.Explain(report);

        Assert.Contains($"Strategy {leader} is ranked first by {metric}={value}", result.Summary);
        Assert.Contains(result.Details, item => item.Contains("return difference of") && item.Contains("maximum drawdown difference of"));
        Assert.Contains(result.Limitations, item => item.Contains("not overall quality or future performance"));
        Assert.Contains(result.Limitations, item => item.Contains("period comparability cannot be verified"));
    }

    [Fact]
    public void Explain_Comparison_TiesDoNotImplySuperiorityOrChangeOrder()
    {
        var a = Completed();
        a.StrategyId = "A";
        var b = Completed();
        b.StrategyId = "B";
        var report = new StrategyComparisonReportService().Build([b, a], StrategyComparisonSortMetric.ReturnPercent);
        var order = report.Entries.ToArray();

        var result = _service.Explain(report);

        Assert.Contains("Strategy A is ranked first", result.Summary);
        Assert.Contains(result.Details, item => item.Contains("tied with B") && item.Contains("not metric superiority"));
        Assert.Equal(order, report.Entries);
        Assert.Equal(1, report.Entries[0].Rank);
    }

    [Fact]
    public void Explain_Comparison_MissingBenchmarkAndFailuresAreNotRanked()
    {
        var missing = Completed();
        missing.StrategyId = "missing";
        var failed = new BacktestSummaryReportResult { StrategyId = "failed", ErrorMessage = "Run failed." };
        var report = new StrategyComparisonReportService().Build([missing, failed],
            StrategyComparisonSortMetric.AlphaPercent, "SPY");

        var result = _service.Explain(report);

        Assert.DoesNotContain("ranked first", result.Summary);
        Assert.Contains(result.Details, item => item.Contains("missing is unranked"));
        Assert.Contains(result.Details, item => item.Contains("failed has no successful result: Run failed."));
        Assert.DoesNotContain(result.Details, item => item.StartsWith("Strategy failed: Return"));
    }

    [Fact]
    public void Explain_EmptyOrUnsortedComparison_DoesNotInventWinner()
    {
        Assert.Equal("No strategy results to explain.", _service.Explain(new StrategyComparisonReport()).Summary);
        var report = new StrategyComparisonReportService().Build([Completed()]);
        Assert.Contains("without an overall winner", _service.Explain(report).Summary);
    }

    [Fact]
    public void Explain_PreservesPrecisionAcrossCultures()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var report = Completed();
            report.ReturnPercent = 1.234567890123456789m;
            Assert.Contains("1.234567890123456789%", _service.Explain(report).Summary);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static BacktestSummaryReportResult Completed() => new()
    {
        IsSuccess = true, Status = BacktestRunStatus.Completed, BacktestRunId = 42,
        StrategyId = "test", StartDate = new(2024, 1, 1), EndDate = new(2024, 1, 31),
        StartCapital = 100, FinalCapital = 112.5m, Profit = 12.5m, ReturnPercent = 12.5m,
        TradesCount = 4, RebalancesCount = 2
    };

    private static BacktestBenchmarkComparison Benchmark(decimal difference) => new()
    {
        Symbol = "SPY", StrategyReturnPercent = 5 + difference, BenchmarkReturnPercent = 5,
        RelativeReturnPercent = difference, AlphaPercent = difference
    };
}
