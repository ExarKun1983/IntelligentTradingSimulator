using System;
using System.Collections.Generic;
using System.Linq;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class StrategyComparisonReportService : IStrategyComparisonReportService
{
    public StrategyComparisonReport Build(
        IReadOnlyList<BacktestSummaryReportResult> summaries,
        StrategyComparisonSortMetric? sortBy = null,
        string? benchmarkSymbol = null)
    {
        if (sortBy.HasValue && !Enum.IsDefined(sortBy.Value))
            throw new ArgumentOutOfRangeException(nameof(sortBy));

        if (sortBy is StrategyComparisonSortMetric.AlphaPercent or StrategyComparisonSortMetric.RelativeReturnPercent &&
            string.IsNullOrWhiteSpace(benchmarkSymbol))
            throw new ArgumentException("Benchmark sorting requires a benchmark symbol.", nameof(benchmarkSymbol));

        var entries = summaries
            .Select(s => new StrategyComparisonReportEntry
            {
                StrategyId = s.StrategyId,
                IsSuccess = s.IsSuccess,
                Status = s.Status,
                ErrorMessage = s.ErrorMessage,
                StartCapital = s.StartCapital,
                FinalCapital = s.FinalCapital,
                ReturnPercent = s.ReturnPercent,
                MaxDrawdownPercent = s.MaxDrawdownPercent,
                DrawdownDurationDays = s.DrawdownDurationDays,
                RecoveryTimeDays = s.RecoveryTimeDays,
                TradesCount = s.TradesCount,
                BenchmarkComparisons = s.BenchmarkComparisons
            })
            .ToList();

        if (sortBy.HasValue)
        {
            var metric = sortBy.Value;
            var values = entries.Select(entry => new
            {
                Entry = entry,
                Value = GetMetricValue(entry, metric, benchmarkSymbol)
            });
            var availableFirst = values.OrderByDescending(item => item.Value.HasValue);
            var ordered = metric == StrategyComparisonSortMetric.MaxDrawdownPercent
                ? availableFirst.ThenBy(item => item.Value)
                : availableFirst.ThenByDescending(item => item.Value);
            var ranked = ordered.ThenBy(item => item.Entry.StrategyId, StringComparer.Ordinal).ToList();

            for (var i = 0; i < ranked.Count; i++)
                ranked[i].Entry.Rank = ranked[i].Value.HasValue ? i + 1 : null;

            entries = ranked.Select(item => item.Entry).ToList();
        }

        return new StrategyComparisonReport
        {
            Entries = entries,
            SortBy = sortBy,
            BenchmarkSymbol = sortBy is StrategyComparisonSortMetric.AlphaPercent or StrategyComparisonSortMetric.RelativeReturnPercent
                ? benchmarkSymbol : null
        };
    }

    private static decimal? GetMetricValue(
        StrategyComparisonReportEntry entry,
        StrategyComparisonSortMetric metric,
        string? benchmarkSymbol)
    {
        if (!entry.IsSuccess || entry.Status != BacktestRunStatus.Completed)
            return null;

        var benchmark = entry.BenchmarkComparisons.FirstOrDefault(
            comparison => string.Equals(comparison.Symbol, benchmarkSymbol, StringComparison.OrdinalIgnoreCase));

        return metric switch
        {
            StrategyComparisonSortMetric.ReturnPercent => entry.ReturnPercent,
            StrategyComparisonSortMetric.MaxDrawdownPercent => entry.MaxDrawdownPercent,
            StrategyComparisonSortMetric.TradesCount => entry.TradesCount,
            StrategyComparisonSortMetric.AlphaPercent => benchmark?.AlphaPercent,
            StrategyComparisonSortMetric.RelativeReturnPercent => benchmark?.RelativeReturnPercent,
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }
}
