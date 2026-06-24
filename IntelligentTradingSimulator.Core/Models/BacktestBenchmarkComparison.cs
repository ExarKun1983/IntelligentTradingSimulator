namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestBenchmarkComparison
{
    public required string Symbol { get; init; }

    public decimal StrategyReturnPercent { get; init; }

    public decimal BenchmarkReturnPercent { get; init; }
}
