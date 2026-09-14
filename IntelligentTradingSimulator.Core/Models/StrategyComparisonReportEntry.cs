namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyComparisonReportEntry
{
    public string StrategyId { get; set; } = string.Empty;

    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public decimal StartCapital { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal ReturnPercent { get; set; }

    public decimal MaxDrawdownPercent { get; set; }

    public int TradesCount { get; set; }
}
