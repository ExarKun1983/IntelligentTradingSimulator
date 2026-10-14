namespace IntelligentTradingSimulator.Core.Models;

/// <summary>
/// Unweighted inputs for one stock on one effective analysis date.
/// Required metrics come from the strategy window; null optional metrics mean
/// unavailable, not zero. Availability is independent of scoring enablement.
/// </summary>
public sealed class StockRankingFeatures
{
    public required string Symbol { get; init; }

    public required DateOnly AnalysisDate { get; init; }

    public required decimal MomentumPercent { get; init; }

    public required decimal AverageVolume { get; init; }

    public required decimal VolumeTrendPercent { get; init; }

    public required decimal VolatilityPercent { get; init; }

    public required bool IsAboveSma5 { get; init; }

    public decimal? Rsi { get; init; }

    public decimal? Atr { get; init; }

    public decimal? Week52High { get; init; }

    public decimal? DistanceFromWeek52HighPercent { get; init; }

    public string? RelativeStrengthBenchmarkSymbol { get; init; }

    public decimal? RelativeStrengthPercent { get; init; }

    public FundamentalData? Fundamentals { get; init; }

    /// <summary>
    /// Adapts the row to the existing scoring contract, retaining the complete
    /// feature row for engines that need date, ATR, or benchmark metadata.
    /// </summary>
    public ScoringContext ToScoringContext() => new()
    {
        Symbol = Symbol,
        MomentumPercent = MomentumPercent,
        AverageVolume = AverageVolume,
        VolumeTrendPercent = VolumeTrendPercent,
        VolatilityPercent = VolatilityPercent,
        IsAboveSma5 = IsAboveSma5,
        Rsi = Rsi,
        DistanceFromWeek52HighPercent = DistanceFromWeek52HighPercent,
        RelativeStrengthPercent = RelativeStrengthPercent,
        Fundamentals = Fundamentals,
        Features = this
    };
}
