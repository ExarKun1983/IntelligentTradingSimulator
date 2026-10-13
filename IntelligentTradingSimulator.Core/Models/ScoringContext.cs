namespace IntelligentTradingSimulator.Core.Models;

/// <summary>
/// Raw, already-calculated analysis metrics handed over to an
/// <see cref="Interfaces.IScoringEngine"/> implementation.
/// </summary>
public sealed class ScoringContext
{
    public string Symbol { get; init; } = string.Empty;

    public decimal MomentumPercent { get; init; }

    public decimal AverageVolume { get; init; }

    public decimal VolumeTrendPercent { get; init; }

    public decimal VolatilityPercent { get; init; }

    public bool IsAboveSma5 { get; init; }

    public decimal? Rsi { get; init; }

    public decimal? DistanceFromWeek52HighPercent { get; init; }

    public decimal? RelativeStrengthPercent { get; init; }

    public FundamentalData? Fundamentals { get; init; }
}
