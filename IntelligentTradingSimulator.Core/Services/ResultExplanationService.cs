using System.Globalization;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class ResultExplanationService : IResultExplanationService
{
    private const string AttributionLimitation =
        "Aggregate report metrics do not attribute returns or drawdowns to holdings or scoring factors; causal drivers and factor differences cannot be determined.";

    public ResultExplanation Explain(BacktestSummaryReportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess)
            return new("Backtest summary unavailable.", [], [result.ErrorMessage ?? "No report metrics available."]);

        if (result.Status != BacktestRunStatus.Completed)
            return new($"Backtest {result.BacktestRunId} is {result.Status}; no completed-outcome explanation generated.", [], []);

        var details = Metrics(result.ReturnPercent, result.MaxDrawdownPercent,
            result.DrawdownDurationDays, result.RecoveryTimeDays, result.TradesCount);
        details.Insert(0,
            $"Capital moved from {Number(result.StartCapital)} to {Number(result.FinalCapital)}; reported profit was {Number(result.Profit)}.");
        details.Add($"{result.RebalancesCount} rebalances were recorded.");
        AddBenchmarks(details, result.BenchmarkComparisons);

        return new(
            $"Strategy {result.StrategyId} returned {Number(result.ReturnPercent)}% over {Date(result.StartDate)} to {Date(result.EndDate)} (run {result.BacktestRunId}).",
            details, [AttributionLimitation]);
    }

    public ResultExplanation Explain(StrategyComparisonReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var details = new List<string>();
        foreach (var entry in report.Entries)
        {
            if (!entry.IsSuccess)
            {
                details.Add($"Strategy {entry.StrategyId} has no successful result: {entry.ErrorMessage ?? "error unavailable"}.");
                continue;
            }

            details.Add($"Strategy {entry.StrategyId}: " + string.Join(" ",
                Metrics(entry.ReturnPercent, entry.MaxDrawdownPercent,
                    entry.DrawdownDurationDays, entry.RecoveryTimeDays, entry.TradesCount)));
            var benchmarks = new List<string>();
            AddBenchmarks(benchmarks, entry.BenchmarkComparisons);
            details.AddRange(benchmarks.Select(text => $"Strategy {entry.StrategyId}: {text}"));
            if (report.SortBy.HasValue && !entry.Rank.HasValue)
                details.Add($"Strategy {entry.StrategyId} is unranked because the selected metric is unavailable.");
        }

        var summary = report.Entries.Count == 0
            ? "No strategy results to explain."
            : "Strategy results are described without an overall winner; no available ranking was supplied.";
        var top = report.TopStrategy;
        if (top is { IsSuccess: true } && report.SortBy is { } metric &&
            Metric(top, metric, report.BenchmarkSymbol) is { } topValue)
        {
            summary = $"Strategy {top.StrategyId} is ranked first by {metric}={Number(topValue)}" +
                (report.BenchmarkSymbol is null ? "." : $" against {report.BenchmarkSymbol}.");
            var ties = report.Entries.Where(entry => entry.IsSuccess &&
                !ReferenceEquals(entry, top) && entry.Rank.HasValue &&
                Metric(entry, metric, report.BenchmarkSymbol) == topValue).ToList();
            if (ties.Count > 0)
                details.Add($"The leading metric is tied with {string.Join(", ", ties.Select(entry => entry.StrategyId))}; ordinal strategy-ID tie-breaking determines order, not metric superiority.");
            foreach (var other in report.Entries.Where(entry => entry.IsSuccess && !ReferenceEquals(entry, top)))
                details.Add($"Compared with {other.StrategyId}, {top.StrategyId} has a return difference of {Number(top.ReturnPercent - other.ReturnPercent)} percentage points and a maximum drawdown difference of {Number(top.MaxDrawdownPercent - other.MaxDrawdownPercent)} percentage points (positive drawdown difference means deeper loss).");
        }

        return new(summary, details,
            [AttributionLimitation,
             "Ranking describes only the selected metric, not overall quality or future performance. Comparison reports do not carry date ranges; period comparability cannot be verified here."]);
    }

    private static List<string> Metrics(decimal returns, decimal drawdown, int duration, int? recovery, int trades) =>
    [
        $"Return was {Number(returns)}%; {trades} trades were executed.",
        $"Maximum drawdown was {Number(drawdown)}%, with {duration} calendar day(s) from peak to trough.",
        drawdown == 0m
            ? "No drawdown was recorded; recovery time is not applicable."
            : recovery.HasValue
                ? $"Recovery from the maximum-drawdown trough took {recovery.Value} calendar day(s)."
                : "The maximum drawdown had not recovered by the end of the recorded period."
    ];

    private static void AddBenchmarks(List<string> details, IReadOnlyList<BacktestBenchmarkComparison> benchmarks)
    {
        if (benchmarks.Count == 0)
            details.Add("Benchmark metrics are unavailable; market-relative performance cannot be assessed.");
        foreach (var benchmark in benchmarks)
        {
            var difference = benchmark.StrategyReturnPercent - benchmark.BenchmarkReturnPercent;
            var outcome = difference > 0m ? "outperformed" : difference < 0m ? "underperformed" : "matched";
            details.Add($"Against {benchmark.Symbol}, strategy return {Number(benchmark.StrategyReturnPercent)}% {outcome} benchmark return {Number(benchmark.BenchmarkReturnPercent)}%; reported relative return was {Number(benchmark.RelativeReturnPercent)} percentage points and alpha was {Number(benchmark.AlphaPercent)} percentage points. Alpha is a simple return difference, not risk-adjusted.");
        }
    }

    private static decimal? Metric(StrategyComparisonReportEntry entry, StrategyComparisonSortMetric metric, string? symbol) =>
        metric switch
        {
            StrategyComparisonSortMetric.ReturnPercent => entry.ReturnPercent,
            StrategyComparisonSortMetric.MaxDrawdownPercent => entry.MaxDrawdownPercent,
            StrategyComparisonSortMetric.TradesCount => entry.TradesCount,
            StrategyComparisonSortMetric.AlphaPercent => Benchmark(entry, symbol)?.AlphaPercent,
            StrategyComparisonSortMetric.RelativeReturnPercent => Benchmark(entry, symbol)?.RelativeReturnPercent,
            _ => null
        };

    private static BacktestBenchmarkComparison? Benchmark(StrategyComparisonReportEntry entry, string? symbol) =>
        entry.BenchmarkComparisons.FirstOrDefault(item =>
            string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase));

    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static string Date(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
