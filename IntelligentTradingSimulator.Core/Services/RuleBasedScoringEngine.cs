using System;

using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

/// <summary>
/// Deterministic scoring engine preserving the original inline formula of
/// <see cref="MarketObservationAnalyzer"/>.
/// </summary>
public sealed class RuleBasedScoringEngine : IScoringEngine
{
    private readonly StrategyConfiguration _strategyConfiguration;

    public RuleBasedScoringEngine(
        StrategyConfiguration? strategyConfiguration = null)
    {
        _strategyConfiguration =
            strategyConfiguration ?? new StrategyConfiguration();
    }

    public decimal CalculateScore(ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var score =
            context.MomentumPercent * 10m
            + context.VolumeTrendPercent * 0.05m
            - context.VolatilityPercent;

        if (!context.IsAboveSma5)
        {
            return StrategyConfiguration.DisqualifiedScore;
        }

        score += 5m;

        if (_strategyConfiguration.UseQualityFilter &&
            context.VolatilityPercent > StrategyConfiguration.QualityVolatilityThresholdPercent)
        {
            return StrategyConfiguration.DisqualifiedScore;
        }

        if (_strategyConfiguration.UseRsi &&
            context.Rsi.HasValue &&
            context.Rsi.Value >= _strategyConfiguration.RsiOverboughtThreshold)
        {
            score -= _strategyConfiguration.RsiOverboughtPenalty;
        }
        else if (_strategyConfiguration.UseRsi &&
                 context.Rsi.HasValue &&
                 context.Rsi.Value >= _strategyConfiguration.RsiPreferredMinimum &&
                 context.Rsi.Value <= _strategyConfiguration.RsiPreferredMaximum)
        {
            score += _strategyConfiguration.RsiPreferredRangeBonus;
        }

        return score;
    }
}
