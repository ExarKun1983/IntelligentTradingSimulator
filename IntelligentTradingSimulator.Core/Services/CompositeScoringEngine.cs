using System;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

/// <summary>
/// Aggregates score-point contributions without cross-stock normalization.
/// Percentage-point factors retain their units; 52-week distance is bounded
/// and mapped to [-1, 1], and RSI uses configured threshold-based points.
/// </summary>
public sealed class CompositeScoringEngine : IScoringEngine
{
    private readonly StrategyConfiguration _configuration;

    public CompositeScoringEngine(StrategyConfiguration? strategyConfiguration = null)
    {
        _configuration = strategyConfiguration ?? new StrategyConfiguration();
    }

    public decimal CalculateScore(ScoringContext context) => CalculateContributions(context).Score;

    public decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
    {
        var result = CalculateContributions(context);
        relativeStrengthScoreContribution = result.RelativeStrengthContribution;
        return result.Score;
    }

    public CompositeScoreResult CalculateContributions(ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateWeight(_configuration.MomentumScoreWeight, nameof(StrategyConfiguration.MomentumScoreWeight));
        ValidateWeight(_configuration.VolumeTrendScoreWeight, nameof(StrategyConfiguration.VolumeTrendScoreWeight));
        ValidateWeight(_configuration.VolatilityScoreWeight, nameof(StrategyConfiguration.VolatilityScoreWeight));

        if (!context.IsAboveSma5 ||
            (_configuration.UseQualityFilter &&
             context.VolatilityPercent > StrategyConfiguration.QualityVolatilityThresholdPercent))
        {
            return new CompositeScoreResult { IsDisqualified = true };
        }

        var rsiContribution = 0m;
        if (_configuration.UseRsi && context.Rsi.HasValue)
        {
            if (context.Rsi.Value >= _configuration.RsiOverboughtThreshold)
            {
                rsiContribution = -_configuration.RsiOverboughtPenalty;
            }
            else if (context.Rsi.Value >= _configuration.RsiPreferredMinimum &&
                     context.Rsi.Value <= _configuration.RsiPreferredMaximum)
            {
                rsiContribution = _configuration.RsiPreferredRangeBonus;
            }
        }

        var week52HighContribution = 0m;
        if (_configuration.Use52WeekHigh && context.DistanceFromWeek52HighPercent.HasValue)
        {
            var boundedDistance = Math.Clamp(context.DistanceFromWeek52HighPercent.Value, -100m, 0m);
            week52HighContribution = (boundedDistance + 50m) / 50m * _configuration.Week52HighScoreWeight;
        }

        return new CompositeScoreResult
        {
            MomentumContribution = context.MomentumPercent * _configuration.MomentumScoreWeight,
            VolumeTrendContribution = context.VolumeTrendPercent * _configuration.VolumeTrendScoreWeight,
            VolatilityContribution = -context.VolatilityPercent * _configuration.VolatilityScoreWeight,
            SmaContribution = 5m,
            RsiContribution = rsiContribution,
            Week52HighContribution = week52HighContribution,
            RelativeStrengthContribution = _configuration.UseRelativeStrength && context.RelativeStrengthPercent.HasValue
                ? context.RelativeStrengthPercent.Value * _configuration.RelativeStrengthScoreWeight
                : 0m
        };
    }

    private static void ValidateWeight(decimal weight, string name)
    {
        if (weight < 0m)
        {
            throw new ArgumentOutOfRangeException(name, "Score weights must be non-negative.");
        }
    }
}
