namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyConfiguration
{
    public const string DefaultId = "default";
    public const int DefaultMaxPositions = 10;
    public const decimal DefaultMaxPositionWeight = 100m;
    public const int DefaultLookbackDays = 90;
    public const decimal QualityVolatilityThresholdPercent = 3m;
    public const decimal DisqualifiedScore = -999m;
    public const decimal DefaultRsiPreferredMinimum = 50m;
    public const decimal DefaultRsiPreferredMaximum = 70m;
    public const decimal DefaultRsiOverboughtThreshold = 80m;
    public const decimal DefaultRsiPreferredRangeBonus = 5m;
    public const decimal DefaultRsiOverboughtPenalty = 5m;

    public string Id { get; set; } = DefaultId;

    public int MaxPositions { get; set; } = DefaultMaxPositions;

    public decimal MaxPositionWeight { get; set; } = DefaultMaxPositionWeight;

    public int LookbackDays { get; set; } = DefaultLookbackDays;

    public bool UseRsi { get; set; } = false;

    public decimal RsiPreferredMinimum { get; set; } = DefaultRsiPreferredMinimum;

    public decimal RsiPreferredMaximum { get; set; } = DefaultRsiPreferredMaximum;

    public decimal RsiOverboughtThreshold { get; set; } = DefaultRsiOverboughtThreshold;

    public decimal RsiPreferredRangeBonus { get; set; } = DefaultRsiPreferredRangeBonus;

    public decimal RsiOverboughtPenalty { get; set; } = DefaultRsiOverboughtPenalty;

    public bool UseRelativeStrength { get; set; } = true;

    public bool Use52WeekHigh { get; set; } = false;

    public bool UseQualityFilter { get; set; } = false;
}
