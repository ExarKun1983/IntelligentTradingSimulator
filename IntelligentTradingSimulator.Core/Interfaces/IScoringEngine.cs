using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

/// <summary>
/// Calculates the ranking score of a single observation from metrics that were
/// already calculated by the analyzer. Implementations may be deterministic
/// (rule based) or, in the future, model driven.
/// </summary>
public interface IScoringEngine
{
    decimal CalculateScore(ScoringContext context);
}
