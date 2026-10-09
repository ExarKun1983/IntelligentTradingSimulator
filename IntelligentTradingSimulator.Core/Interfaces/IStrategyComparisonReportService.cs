using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IStrategyComparisonReportService
{
    StrategyComparisonReport Build(
        IReadOnlyList<BacktestSummaryReportResult> summaries,
        StrategyComparisonSortMetric? sortBy = null,
        string? benchmarkSymbol = null);
}
