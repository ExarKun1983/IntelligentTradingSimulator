using System.Collections.Generic;
using System.Linq;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class StrategyComparisonReportService : IStrategyComparisonReportService
{
    public StrategyComparisonReport Build(IReadOnlyList<BacktestSummaryReportResult> summaries)
    {
        var entries = summaries
            .Select(s => new StrategyComparisonReportEntry
            {
                StrategyId = s.StrategyId,
                IsSuccess = s.IsSuccess,
                ErrorMessage = s.ErrorMessage,
                StartCapital = s.StartCapital,
                FinalCapital = s.FinalCapital,
                ReturnPercent = s.ReturnPercent,
                MaxDrawdownPercent = s.MaxDrawdownPercent,
                TradesCount = s.TradesCount
            })
            .ToList();

        return new StrategyComparisonReport { Entries = entries };
    }
}
