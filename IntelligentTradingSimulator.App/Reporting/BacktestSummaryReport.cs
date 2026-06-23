using System;

namespace IntelligentTradingSimulator.App.Reporting;

public sealed class BacktestSummaryReport
{
    public int RunId { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public decimal InitialCapital { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal TotalProfit { get; set; }

    public decimal TotalReturnPercent { get; set; }

    public int TradesCount { get; set; }

    public int RebalancesCount { get; set; }

    public int ProcessedDays { get; set; }

    public decimal BestDayReturnPercent { get; set; }

    public decimal WorstDayReturnPercent { get; set; }
}
