using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

/// <summary>
/// Deterministic scoring engine preserving the original inline formula of
/// <see cref="MarketObservationAnalyzer"/>.
/// </summary>
public sealed class RuleBasedScoringEngine : IScoringEngine
{
    private readonly CompositeScoringEngine _compositeScoringEngine;

    public RuleBasedScoringEngine(
        StrategyConfiguration? strategyConfiguration = null)
    {
        _compositeScoringEngine = new CompositeScoringEngine(strategyConfiguration);
    }

    public decimal CalculateScore(ScoringContext context)
    {
        return _compositeScoringEngine.CalculateScore(context);
    }

    public decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
    {
        return _compositeScoringEngine.CalculateScore(context, out relativeStrengthScoreContribution);
    }
}
