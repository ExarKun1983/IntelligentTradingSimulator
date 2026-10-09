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

        var results = new List<StrategyConfiguration>();

        foreach (var section in strategiesSection.GetChildren())
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
        var config = new StrategyConfiguration
        {
            Id = id,
            MaxPositions = section.GetValue<int?>("MaxPositions")
                ?? StrategyConfiguration.DefaultMaxPositions,
            MaxPositionWeight = section.GetValue<decimal?>("MaxPositionWeight")
                ?? StrategyConfiguration.DefaultMaxPositionWeight,
            LookbackDays = section.GetValue<int?>("LookbackDays")
                ?? StrategyConfiguration.DefaultLookbackDays,
            UseRsi = section.GetValue<bool?>("UseRsi")
                ?? false,
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
            Use52WeekHigh = section.GetValue<bool?>("Use52WeekHigh")
                ?? false,
            Week52HighScoreWeight = section.GetValue<decimal?>("Week52HighScoreWeight")
                ?? StrategyConfiguration.DefaultWeek52HighScoreWeight,
            UseQualityFilter = section.GetValue<bool?>("UseQualityFilter")
                ?? false
        };

        if (config.MaxPositions <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:MaxPositions' must be greater than zero.");
        }

        if (config.MaxPositions > MaximumStrategyComparisonLimit)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{id}:MaxPositions' must be less than or equal to {MaximumStrategyComparisonLimit}.");
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

        return config;
    }
}
