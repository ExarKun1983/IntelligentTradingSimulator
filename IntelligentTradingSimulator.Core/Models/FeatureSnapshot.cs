namespace IntelligentTradingSimulator.Core.Models;

public sealed class FeatureSnapshot
{
    public int Id { get; set; }

    public required string Symbol { get; init; }

    public required DateOnly AnalysisDate { get; init; }

    public required string StrategyId { get; init; }

    public int? BacktestRunId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required StockRankingFeatures Features { get; init; }

    public decimal? Score { get; init; }

    public required string StrategyConfigurationJson { get; init; }

    public string? FactorContributionsJson { get; init; }
}
