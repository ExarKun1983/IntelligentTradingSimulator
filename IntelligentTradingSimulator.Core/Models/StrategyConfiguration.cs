namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyConfiguration
{
    public const string DefaultId = "default";
    public const int DefaultMaxPositions = 10;
    public const decimal DefaultMaxPositionWeight = 100m;

    public string Id { get; set; } = DefaultId;

    public int MaxPositions { get; set; } = DefaultMaxPositions;

    public decimal MaxPositionWeight { get; set; } = DefaultMaxPositionWeight;
}
