using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyConfigurationLoader
{
    private const int MaximumStrategyComparisonLimit = 100;

    public static IReadOnlyList<StrategyConfiguration> LoadAll(IConfiguration configuration)
    {
        var strategiesSection = configuration.GetSection("Strategies");

        if (!strategiesSection.Exists())
        {
            throw new InvalidOperationException(
                "Configuration section 'Strategies' is missing or empty.");
        }

        var strategySections = new List<IConfigurationSection>(strategiesSection.GetChildren());
        if (strategySections.Count > MaximumStrategyComparisonLimit)
        {
            throw new InvalidOperationException(
                $"Configuration section 'Strategies' contains {strategySections.Count} strategy definitions; the maximum strategy comparison limit is {MaximumStrategyComparisonLimit}.");
        }

        var results = new List<StrategyConfiguration>();

        foreach (var section in strategySections)
        {
            results.Add(BindAndValidate(section.Key, section));
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException(
                "Configuration section 'Strategies' contains no strategy definitions.");
        }

        return results;
    }

    public static StrategyConfiguration Load(IConfiguration configuration)
    {
        var activeStrategyId =
            configuration.GetValue<string>("ActiveStrategy")
            ?? StrategyConfiguration.DefaultId;
        activeStrategyId = activeStrategyId.Trim();

        if (string.IsNullOrWhiteSpace(activeStrategyId))
        {
            throw new InvalidOperationException(
                "Configuration value 'ActiveStrategy' must not be empty.");
        }

        var strategySection =
            configuration.GetSection("Strategies")
                .GetSection(activeStrategyId);

        if (!strategySection.Exists())
        {
            throw new InvalidOperationException(
                $"Configuration section 'Strategies:{activeStrategyId}' is missing for selected active strategy.");
        }

        return BindAndValidate(activeStrategyId, strategySection);
    }

    private static StrategyConfiguration BindAndValidate(string id, IConfigurationSection section)
    {
        var scoringEngine = ScoringEngineType.Composite;
        if (section["ScoringEngine"] is { } engineName &&
            (!Enum.TryParse(engineName.Trim(), ignoreCase: true, out scoringEngine) ||
             !Enum.IsDefined(scoringEngine) ||
             !string.Equals(engineName.Trim(), scoringEngine.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:ScoringEngine' must be Composite, RuleBased or AI.");

        var config = new StrategyConfiguration
        {
            Id = id,
            ScoringEngine = scoringEngine,
            MaxPositions = section.GetValue<int?>("MaxPositions")
                ?? StrategyConfiguration.DefaultMaxPositions,
            MaxPositionWeight = section.GetValue<decimal?>("MaxPositionWeight")
                ?? StrategyConfiguration.DefaultMaxPositionWeight,
            StopLossPercent = section.GetValue<decimal?>("StopLossPercent"),
            TrailingStopPercent = section.GetValue<decimal?>("TrailingStopPercent"),
            LookbackDays = section.GetValue<int?>("LookbackDays")
                ?? StrategyConfiguration.DefaultLookbackDays,
            MomentumScoreWeight = section.GetValue<decimal?>("MomentumScoreWeight")
                ?? StrategyConfiguration.DefaultMomentumScoreWeight,
            VolumeTrendScoreWeight = section.GetValue<decimal?>("VolumeTrendScoreWeight")
                ?? StrategyConfiguration.DefaultVolumeTrendScoreWeight,
            VolatilityScoreWeight = section.GetValue<decimal?>("VolatilityScoreWeight")
                ?? StrategyConfiguration.DefaultVolatilityScoreWeight,
            SmaScoreWeight = section.GetValue<decimal?>("SmaScoreWeight")
                ?? StrategyConfiguration.DefaultSmaScoreWeight,
            UseRsi = section.GetValue<bool?>("UseRsi")
                ?? false,
            RsiScoreWeight = section.GetValue<decimal?>("RsiScoreWeight")
                ?? StrategyConfiguration.DefaultRsiScoreWeight,
            RsiPreferredMinimum = section.GetValue<decimal?>("RsiPreferredMinimum")
                ?? StrategyConfiguration.DefaultRsiPreferredMinimum,
            RsiPreferredMaximum = section.GetValue<decimal?>("RsiPreferredMaximum")
                ?? StrategyConfiguration.DefaultRsiPreferredMaximum,
            RsiOverboughtThreshold = section.GetValue<decimal?>("RsiOverboughtThreshold")
                ?? StrategyConfiguration.DefaultRsiOverboughtThreshold,
            RsiPreferredRangeBonus = section.GetValue<decimal?>("RsiPreferredRangeBonus")
                ?? StrategyConfiguration.DefaultRsiPreferredRangeBonus,
            RsiOverboughtPenalty = section.GetValue<decimal?>("RsiOverboughtPenalty")
                ?? StrategyConfiguration.DefaultRsiOverboughtPenalty,
            UseRelativeStrength = section.GetValue<bool?>("UseRelativeStrength")
                ?? true,
            RelativeStrengthBenchmarkSymbol =
                (section.GetValue<string>("RelativeStrengthBenchmarkSymbol") ?? "SPY").Trim(),
            RelativeStrengthScoreWeight = section.GetValue<decimal?>("RelativeStrengthScoreWeight")
                ?? StrategyConfiguration.DefaultRelativeStrengthScoreWeight,
            Use52WeekHigh = section.GetValue<bool?>("Use52WeekHigh")
                ?? false,
            Week52HighScoreWeight = section.GetValue<decimal?>("Week52HighScoreWeight")
                ?? StrategyConfiguration.DefaultWeek52HighScoreWeight,
            UseQualityFilter = section.GetValue<bool?>("UseQualityFilter")
                ?? false,
            QualityMinimumRevenueGrowthPercent = section.GetValue<decimal?>("QualityMinimumRevenueGrowthPercent"),
            QualityMinimumEpsGrowthPercent = section.GetValue<decimal?>("QualityMinimumEpsGrowthPercent"),
            QualityMinimumMarginsPercent = section.GetValue<decimal?>("QualityMinimumMarginsPercent"),
            QualityMaximumDebtToEquity = section.GetValue<decimal?>("QualityMaximumDebtToEquity"),
            QualityFailureAction = section.GetValue<QualityFilterAction?>("QualityFailureAction")
                ?? QualityFilterAction.Exclude,
            QualityMissingDataAction = section.GetValue<QualityFilterAction?>("QualityMissingDataAction")
                ?? QualityFilterAction.Neutral,
            QualityPenalty = section.GetValue<decimal?>("QualityPenalty") ?? 5m
        };

        foreach (var (name, action) in new[]
        {
            (nameof(StrategyConfiguration.QualityFailureAction), config.QualityFailureAction),
            (nameof(StrategyConfiguration.QualityMissingDataAction), config.QualityMissingDataAction)
        })
        {
            if (!Enum.IsDefined(action))
                throw new InvalidOperationException(
                    $"Configuration value 'Strategies:{id}:{name}' must be Neutral, Penalize or Exclude.");
        }
        if (config.QualityPenalty < 0m)
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:QualityPenalty' must be non-negative.");
        if (config.QualityMaximumDebtToEquity is < 0m)
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:QualityMaximumDebtToEquity' must be non-negative.");

        if (string.IsNullOrWhiteSpace(config.RelativeStrengthBenchmarkSymbol))
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RelativeStrengthBenchmarkSymbol' must not be empty.");
        }

        var benchmarkSymbol = Array.Find(BenchmarkUniverse.DefaultSymbols,
            symbol => string.Equals(symbol, config.RelativeStrengthBenchmarkSymbol, StringComparison.OrdinalIgnoreCase));
        if (benchmarkSymbol is null)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RelativeStrengthBenchmarkSymbol' must be a supported benchmark: {string.Join(", ", BenchmarkUniverse.DefaultSymbols)}.");
        }
        config.RelativeStrengthBenchmarkSymbol = benchmarkSymbol;

        if (config.MaxPositions <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:MaxPositions' must be greater than zero.");
        }

        if (config.MaxPositionWeight <= 0m || config.MaxPositionWeight > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:MaxPositionWeight' must be greater than zero and less than or equal to 100.");
        }

        if (config.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:LookbackDays' must be greater than zero.");
        }

        if (config.StopLossPercent is <= 0m or > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:StopLossPercent' must be greater than zero and less than or equal to 100.");
        }

        if (config.TrailingStopPercent is <= 0m or > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:TrailingStopPercent' must be greater than zero and less than or equal to 100.");
        }

        if (config.RsiPreferredMinimum < 0m || config.RsiPreferredMinimum > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiPreferredMinimum' must be between 0 and 100.");
        }

        if (config.RsiPreferredMaximum < 0m || config.RsiPreferredMaximum > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiPreferredMaximum' must be between 0 and 100.");
        }

        if (config.RsiPreferredMinimum > config.RsiPreferredMaximum)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiPreferredMinimum' ({config.RsiPreferredMinimum}) must be less than or equal to 'Strategies:{id}:RsiPreferredMaximum' ({config.RsiPreferredMaximum}).");
        }

        if (config.RsiOverboughtThreshold < 0m || config.RsiOverboughtThreshold > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiOverboughtThreshold' must be between 0 and 100.");
        }

        if (config.RsiOverboughtThreshold < config.RsiPreferredMaximum)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiOverboughtThreshold' must be greater than or equal to 'Strategies:{id}:RsiPreferredMaximum'.");
        }

        if (config.RsiPreferredRangeBonus < 0m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiPreferredRangeBonus' must be greater than or equal to zero.");
        }

        if (config.RsiOverboughtPenalty < 0m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RsiOverboughtPenalty' must be greater than or equal to zero.");
        }

        if (config.Week52HighScoreWeight < 0m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:Week52HighScoreWeight' must be greater than or equal to zero.");
        }

        if (config.RelativeStrengthScoreWeight < 0m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:RelativeStrengthScoreWeight' must be greater than or equal to zero.");
        }

        foreach (var (name, weight) in new[]
        {
            (nameof(StrategyConfiguration.MomentumScoreWeight), config.MomentumScoreWeight),
            (nameof(StrategyConfiguration.VolumeTrendScoreWeight), config.VolumeTrendScoreWeight),
            (nameof(StrategyConfiguration.VolatilityScoreWeight), config.VolatilityScoreWeight),
            (nameof(StrategyConfiguration.SmaScoreWeight), config.SmaScoreWeight),
            (nameof(StrategyConfiguration.RsiScoreWeight), config.RsiScoreWeight)
        })
        {
            if (weight < 0m)
            {
                throw new InvalidOperationException(
                    $"Configuration value 'Strategies:{id}:{name}' must be greater than or equal to zero.");
            }
        }

        if (config.MomentumScoreWeight == 0m &&
            config.VolumeTrendScoreWeight == 0m &&
            config.VolatilityScoreWeight == 0m &&
            config.SmaScoreWeight == 0m &&
            !(config.UseRsi && config.RsiScoreWeight > 0m &&
              (config.RsiPreferredRangeBonus > 0m || config.RsiOverboughtPenalty > 0m)) &&
            !(config.UseRelativeStrength && config.RelativeStrengthScoreWeight > 0m) &&
            !(config.Use52WeekHigh && config.Week52HighScoreWeight > 0m))
        {
            throw new InvalidOperationException(
                $"Configuration section 'Strategies:{id}' must have at least one active scoring factor with a positive weight; eligibility and quality filters do not count as scoring factors.");
        }

        return config;
    }
}
