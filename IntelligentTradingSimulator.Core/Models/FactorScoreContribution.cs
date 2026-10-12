namespace IntelligentTradingSimulator.Core.Models;

public enum FactorScoreStatus
{
    Applied,
    Disabled,
    Unavailable,
    Disqualified
}

/// <summary>
/// Applied score points, not the raw factor value. Unavailable factors have no
/// contribution value; disabled and disqualified factors have zero applied points.
/// </summary>
public sealed record FactorScoreContribution(
    string Factor,
    decimal? Contribution,
    FactorScoreStatus Status);
