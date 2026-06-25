namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestSummaryReportResult
{
    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public int BacktestRunId { get; set; }

    public string RunName { get; set; } = string.Empty;

    public decimal StartCapital { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal Profit { get; set; }

    public decimal ReturnPercent { get; set; }

    public decimal MaxDrawdownPercent { get; set; }

    public int TradesCount { get; set; }

    public int RebalancesCount { get; set; }

    public IReadOnlyList<BacktestBenchmarkComparison> BenchmarkComparisons { get; set; } = [];
}
