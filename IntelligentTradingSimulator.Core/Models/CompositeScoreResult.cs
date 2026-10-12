namespace IntelligentTradingSimulator.Core.Models;

/// <summary>
/// Applied score-point contributions, in deterministic aggregation order.
/// Disqualified observations have no applied contributions.
/// </summary>
public sealed class CompositeScoreResult
{
    public bool IsDisqualified { get; init; }

    public decimal MomentumContribution { get; init; }

    public decimal VolumeTrendContribution { get; init; }

    public decimal VolatilityContribution { get; init; }

    public decimal SmaContribution { get; init; }

    public decimal RsiContribution { get; init; }

    public decimal Week52HighContribution { get; init; }

    public decimal RelativeStrengthContribution { get; init; }

    public decimal Score => IsDisqualified
        ? StrategyConfiguration.DisqualifiedScore
        : MomentumContribution + VolumeTrendContribution + VolatilityContribution
          + SmaContribution + RsiContribution + Week52HighContribution
          + RelativeStrengthContribution;
}
