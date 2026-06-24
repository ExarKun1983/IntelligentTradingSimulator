namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestBenchmarkComparison
{
    public string Symbol { get; set; } = string.Empty;

    public decimal StrategyReturnPercent { get; set; }

    public decimal BenchmarkReturnPercent { get; set; }
}
