using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyConfigurationLoader
{
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
            UseRelativeStrength = section.GetValue<bool?>("UseRelativeStrength")
                ?? true,
            Use52WeekHigh = section.GetValue<bool?>("Use52WeekHigh")
                ?? false,
            UseQualityFilter = section.GetValue<bool?>("UseQualityFilter")
                ?? false
        };

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

        return config;
    }
}
