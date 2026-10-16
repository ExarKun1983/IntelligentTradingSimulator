using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestSummaryReportResult
{
    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public int BacktestRunId { get; set; }

    public BacktestRunStatus Status { get; set; }

    public string RunName { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public DateTime? HistoricalStartDate { get; set; }

    public decimal StartCapital { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal Profit { get; set; }

    public decimal ReturnPercent { get; set; }

    public decimal MaxDrawdownPercent { get; set; }

    public int DrawdownDurationDays { get; set; }

    public int? RecoveryTimeDays { get; set; }

    public int TradesCount { get; set; }

    public int RebalancesCount { get; set; }

    public IReadOnlyList<BacktestBenchmarkComparison> BenchmarkComparisons { get; set; } = [];
}
