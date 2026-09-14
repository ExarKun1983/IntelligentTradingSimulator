using System.Collections.Generic;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyComparisonReport
{
    public IReadOnlyList<StrategyComparisonReportEntry> Entries { get; set; } = [];
}
