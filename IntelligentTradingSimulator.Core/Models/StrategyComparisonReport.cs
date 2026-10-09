using System.Collections.Generic;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyComparisonReport
{
    public IReadOnlyList<StrategyComparisonReportEntry> Entries { get; set; } = [];

    public StrategyComparisonSortMetric? SortBy { get; set; }

    public string? BenchmarkSymbol { get; set; }

    public StrategyComparisonReportEntry? TopStrategy =>
        Entries.Count > 0 && Entries[0].Rank == 1 ? Entries[0] : null;
}
