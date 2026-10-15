using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IResultExplanationService
{
    ResultExplanation Explain(BacktestSummaryReportResult result);

    ResultExplanation Explain(StrategyComparisonReport report);
}
