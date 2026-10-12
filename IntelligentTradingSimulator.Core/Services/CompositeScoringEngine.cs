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

    public decimal CalculateScore(
        ScoringContext context,
        out decimal? relativeStrengthScoreContribution,
        out IReadOnlyList<FactorScoreContribution>? factorContributions)
    {
        var result = CalculateContributions(context);
        relativeStrengthScoreContribution = result.RelativeStrengthContribution;

        FactorScoreContribution Factor(
            string name, decimal contribution, bool enabled = true, bool available = true)
        {
            var status = !available ? FactorScoreStatus.Unavailable
                : !enabled ? FactorScoreStatus.Disabled
                : result.IsDisqualified ? FactorScoreStatus.Disqualified
                : FactorScoreStatus.Applied;
            return new FactorScoreContribution(name, available ? contribution : null, status);
        }

        var factors = new List<FactorScoreContribution>
        {
            Factor("Momentum", result.MomentumContribution, _configuration.MomentumScoreWeight != 0m),
            Factor("VolumeTrend", result.VolumeTrendContribution, _configuration.VolumeTrendScoreWeight != 0m),
            Factor("Volatility", result.VolatilityContribution, _configuration.VolatilityScoreWeight != 0m),
            Factor("SMA5", result.SmaContribution, _configuration.SmaScoreWeight != 0m),
            Factor("RSI", result.RsiContribution,
                _configuration.UseRsi && _configuration.RsiScoreWeight != 0m, context.Rsi.HasValue),
            Factor("52WeekHigh", result.Week52HighContribution,
                _configuration.Use52WeekHigh && _configuration.Week52HighScoreWeight != 0m,
                context.DistanceFromWeek52HighPercent.HasValue),
            Factor("RelativeStrength", result.RelativeStrengthContribution,
                _configuration.UseRelativeStrength && _configuration.RelativeStrengthScoreWeight != 0m,
                context.RelativeStrengthPercent.HasValue)
        };
        if (result.IsDisqualified)
        {
            factors.Add(new FactorScoreContribution(
                "Disqualification", StrategyConfiguration.DisqualifiedScore, FactorScoreStatus.Applied));
        }

        factorContributions = factors.AsReadOnly();
        return result.Score;
    }

    public CompositeScoreResult CalculateContributions(ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateWeight(_configuration.MomentumScoreWeight, nameof(StrategyConfiguration.MomentumScoreWeight));
        ValidateWeight(_configuration.VolumeTrendScoreWeight, nameof(StrategyConfiguration.VolumeTrendScoreWeight));
        ValidateWeight(_configuration.VolatilityScoreWeight, nameof(StrategyConfiguration.VolatilityScoreWeight));
        ValidateWeight(_configuration.SmaScoreWeight, nameof(StrategyConfiguration.SmaScoreWeight));
        ValidateWeight(_configuration.RsiScoreWeight, nameof(StrategyConfiguration.RsiScoreWeight));
        ValidateWeight(_configuration.RsiPreferredRangeBonus, nameof(StrategyConfiguration.RsiPreferredRangeBonus));
        ValidateWeight(_configuration.RsiOverboughtPenalty, nameof(StrategyConfiguration.RsiOverboughtPenalty));
        ValidateWeight(_configuration.RelativeStrengthScoreWeight, nameof(StrategyConfiguration.RelativeStrengthScoreWeight));
        ValidateWeight(_configuration.Week52HighScoreWeight, nameof(StrategyConfiguration.Week52HighScoreWeight));

        if (!context.IsAboveSma5 ||
            (_configuration.UseQualityFilter &&
             context.VolatilityPercent > StrategyConfiguration.QualityVolatilityThresholdPercent))
        {
            return new CompositeScoreResult { IsDisqualified = true };
        }

        var rsiContribution = 0m;
        if (_configuration.UseRsi && _configuration.RsiScoreWeight != 0m && context.Rsi.HasValue)
        {
            if (context.Rsi.Value >= _configuration.RsiOverboughtThreshold)
            {
                rsiContribution = -_configuration.RsiOverboughtPenalty * _configuration.RsiScoreWeight;
            }
            else if (context.Rsi.Value >= _configuration.RsiPreferredMinimum &&
                     context.Rsi.Value <= _configuration.RsiPreferredMaximum)
            {
                rsiContribution = _configuration.RsiPreferredRangeBonus * _configuration.RsiScoreWeight;
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
            SmaContribution = _configuration.SmaScoreWeight,
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
