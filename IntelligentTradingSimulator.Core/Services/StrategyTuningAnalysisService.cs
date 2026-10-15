using System.Globalization;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class StrategyTuningAnalysisService(
    IStrategyComparisonReportService comparisonService) : IStrategyTuningAnalysisService
{
    private const decimal CapitalRoundingHalfUnit = 0.00005m;
    private const decimal ReturnRoundingHalfUnit = 0.00005m;

    public StrategyTuningReport Analyze(StrategyTuningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SchemaVersion != 1 || string.IsNullOrWhiteSpace(request.BenchmarkSymbol) ||
            request.MinimumReturnPercent < 0m || request.HighRiskDrawdownPercent is <= 0m or > 100m ||
            request.Runs is null || request.Runs.Count < 2)
            throw new ArgumentException("Supply schema 1, a benchmark, at least two runs, and valid return/drawdown thresholds.", nameof(request));

        if (request.Runs.Any(run => run?.Summary is null ||
                run.Summary.BacktestRunId <= 0 || string.IsNullOrWhiteSpace(run.Summary.StrategyId)) ||
            request.Runs.GroupBy(run => run.Summary.BacktestRunId).Any(group => group.Count() > 1))
            throw new ArgumentException("Each run needs a unique positive run ID and a strategy ID.", nameof(request));

        var benchmarkSymbol = request.BenchmarkSymbol.Trim().ToUpperInvariant();
        var ordered = request.Runs.OrderBy(run => run.Summary.BacktestRunId).ToList();
        var completed = ordered.Where(run => IsCompleted(run.Summary)).ToList();
        foreach (var run in ordered)
        {
            var summary = run.Summary;
            if (run.Configuration is not null && run.Configuration.Id != summary.StrategyId)
                throw new ArgumentException("Configuration IDs must match their historical strategy IDs.", nameof(request));
            if (!IsCompleted(summary))
                continue;
            if (summary.StartDate == default || summary.StartDate > summary.EndDate ||
                summary.StartCapital <= 0m || summary.MaxDrawdownPercent is < 0m or > 100m ||
                summary.TradesCount < 0 || summary.DrawdownDurationDays < 0 || summary.RecoveryTimeDays < 0 ||
                summary.BenchmarkComparisons is null ||
                summary.BenchmarkComparisons.Any(b => b is null || string.IsNullOrWhiteSpace(b.Symbol)))
                throw new ArgumentException("Completed runs require valid dates, capital, and risk/trade metrics.", nameof(request));
            if (!HasConsistentReturn(summary))
                throw new ArgumentException("Completed runs must have capital values consistent with their reported return.", nameof(request));
            var cohort = completed[0].Summary;
            if (summary.StartDate != cohort.StartDate || summary.EndDate != cohort.EndDate ||
                summary.StartCapital != cohort.StartCapital)
                throw new ArgumentException("Compare runs over the same dates and initial capital.", nameof(request));
            if (summary.BenchmarkComparisons.Count(b => string.Equals(b.Symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase)) > 1)
                throw new ArgumentException("A run cannot contain duplicate selected benchmark metrics.", nameof(request));
            var benchmark = summary.BenchmarkComparisons.SingleOrDefault(
                b => string.Equals(b.Symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase));
            if (benchmark is not null &&
                (benchmark.StrategyReturnPercent != summary.ReturnPercent ||
                 benchmark.RelativeReturnPercent != summary.ReturnPercent - benchmark.BenchmarkReturnPercent ||
                 benchmark.AlphaPercent != benchmark.RelativeReturnPercent))
                throw new ArgumentException("Selected benchmark metrics must agree with the summary return.", nameof(request));
        }

        var benchmarkReturns = completed.SelectMany(run => run.Summary.BenchmarkComparisons)
            .Where(b => string.Equals(b.Symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase))
            .Select(b => b.BenchmarkReturnPercent).Distinct().ToList();
        if (benchmarkReturns.Count > 1)
            throw new ArgumentException("Selected benchmark returns must match across the cohort.", nameof(request));

        var comparison = comparisonService.Build(ordered.Select(run => run.Summary).ToList(),
            StrategyComparisonSortMetric.ReturnPercent);
        // Stable queues retain run identity when a strategy ID and return repeat.
        var entriesByResult = comparison.Entries
            .GroupBy(entry => (entry.StrategyId, entry.ReturnPercent, entry.IsSuccess, entry.Status))
            .ToDictionary(group => group.Key, group => new Queue<StrategyComparisonReportEntry>(group));
        var analyses = ordered.Select(run =>
        {
            var summary = run.Summary;
            var entry = entriesByResult[(summary.StrategyId, summary.ReturnPercent, summary.IsSuccess, summary.Status)].Dequeue();
            return AnalyzeRun(run, entry, request, benchmarkSymbol, completed.Count);
        }).ToList();
        return new StrategyTuningReport
        {
            BenchmarkSymbol = benchmarkSymbol,
            MinimumReturnPercent = request.MinimumReturnPercent,
            HighRiskDrawdownPercent = request.HighRiskDrawdownPercent,
            StartDate = completed.FirstOrDefault()?.Summary.StartDate,
            EndDate = completed.FirstOrDefault()?.Summary.EndDate,
            Analyses = analyses
        };
    }

    private static StrategyTuningAnalysis AnalyzeRun(
        StrategyTuningRun run, StrategyComparisonReportEntry metrics,
        StrategyTuningRequest request, string benchmarkSymbol, int completedCount)
    {
        var summary = run.Summary;
        var strengths = new List<string>();
        var weaknesses = new List<string>();
        var limitations = new List<string>();
        var configurationJson = run.Configuration is null ? null : JsonSerializer.Serialize(run.Configuration);
        if (configurationJson is null)
            limitations.Add("Historical parameter snapshot is missing; no parameter recommendation can be made.");
        if (!IsCompleted(summary))
        {
            limitations.Add($"Run excluded: unsuccessful or incomplete ({summary.Status}). {summary.ErrorMessage}".Trim());
            return new(summary.BacktestRunId, summary.StrategyId, metrics, configurationJson,
                false, false, false, false, strengths, weaknesses, limitations);
        }

        if (completedCount < 2)
            limitations.Add("Fewer than two completed runs; comparative evidence is insufficient.");
        var benchmark = summary.BenchmarkComparisons.SingleOrDefault(
            b => string.Equals(b.Symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase));
        if (benchmark is null)
            limitations.Add($"Selected benchmark {benchmarkSymbol} is unavailable; outperformance is unknown.");
        else if (benchmark.RelativeReturnPercent > 0m)
            strengths.Add($"Outperformed {benchmarkSymbol} by {Format(benchmark.RelativeReturnPercent)} percentage points.");
        else
            weaknesses.Add($"Did not outperform {benchmarkSymbol} ({Format(benchmark.RelativeReturnPercent)} percentage points).");

        var highReturn = summary.ReturnPercent > request.MinimumReturnPercent;
        if (highReturn)
            strengths.Add($"Return {Format(summary.ReturnPercent)}% exceeds research threshold {Format(request.MinimumReturnPercent)}%.");
        else
            weaknesses.Add($"Return {Format(summary.ReturnPercent)}% does not exceed research threshold {Format(request.MinimumReturnPercent)}%.");
        var highRisk = summary.MaxDrawdownPercent >= request.HighRiskDrawdownPercent;
        if (highRisk)
            weaknesses.Add($"High risk: maximum drawdown {Format(summary.MaxDrawdownPercent)}% reaches threshold {Format(request.HighRiskDrawdownPercent)}%.");
        else
            strengths.Add($"Maximum drawdown {Format(summary.MaxDrawdownPercent)}% is below the research risk threshold.");
        var unrecovered = summary.MaxDrawdownPercent > 0m && summary.RecoveryTimeDays is null;
        if (unrecovered)
            weaknesses.Add("Maximum drawdown had not recovered by the end of the run.");
        if (summary.TradesCount == 0)
            weaknesses.Add("No executed trades; evidence of an active strategy is insufficient.");

        var highPerforming = highReturn && benchmark?.RelativeReturnPercent > 0m;
        var promising = highPerforming && !highRisk && !unrecovered && summary.TradesCount > 0 &&
                        configurationJson is not null && completedCount >= 2;
        return new(summary.BacktestRunId, summary.StrategyId, metrics, configurationJson,
            true, highPerforming, highRisk, promising, strengths, weaknesses, limitations);
    }

    private static bool IsCompleted(BacktestSummaryReportResult summary) =>
        summary.IsSuccess && summary.Status == BacktestRunStatus.Completed;

    private static bool HasConsistentReturn(BacktestSummaryReportResult summary)
    {
        var minimumStartCapital = summary.StartCapital - CapitalRoundingHalfUnit;
        if (minimumStartCapital <= 0m)
            return false;

        var minimumReturn = ((summary.FinalCapital - CapitalRoundingHalfUnit) /
                            (summary.StartCapital + CapitalRoundingHalfUnit) - 1m) * 100m;
        var maximumReturn = ((summary.FinalCapital + CapitalRoundingHalfUnit) /
                            minimumStartCapital - 1m) * 100m;
        return summary.ReturnPercent >= minimumReturn - ReturnRoundingHalfUnit &&
               summary.ReturnPercent <= maximumReturn + ReturnRoundingHalfUnit;
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
