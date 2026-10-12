using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

/// <summary>
/// Calculates the ranking score of a single observation from metrics that were
/// already calculated by the analyzer. Implementations may be deterministic
/// (rule based) or, in the future, model driven.
/// </summary>
public interface IScoringEngine
{
    /// <param name="context">Metrics calculated for a single symbol.</param>
    /// <returns>
    /// The ranking score. Implementations may return
    /// <see cref="Models.StrategyConfiguration.DisqualifiedScore"/> to mark the
    /// symbol as not eligible for the ranking.
    /// </returns>
    decimal CalculateScore(ScoringContext context);

    /// <summary>
    /// Calculates the score and reports the applied Relative Strength contribution.
    /// Engines without factor diagnostics leave the contribution unavailable.
    /// </summary>
    decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
    {
        relativeStrengthScoreContribution = null;
        return CalculateScore(context);
    }
}
