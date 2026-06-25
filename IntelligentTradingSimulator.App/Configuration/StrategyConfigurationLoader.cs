using System;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyConfigurationLoader
{
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

        var strategyConfiguration = new StrategyConfiguration
        {
            Id = activeStrategyId,
            MaxPositions = strategySection.GetValue<int?>("MaxPositions")
                ?? StrategyConfiguration.DefaultMaxPositions,
            MaxPositionWeight = strategySection.GetValue<decimal?>("MaxPositionWeight")
                ?? StrategyConfiguration.DefaultMaxPositionWeight,
            LookbackDays = strategySection.GetValue<int?>("LookbackDays")
                ?? StrategyConfiguration.DefaultLookbackDays,
            UseRsi = strategySection.GetValue<bool?>("UseRsi")
                ?? false,
            UseRelativeStrength = strategySection.GetValue<bool?>("UseRelativeStrength")
                ?? true,
            Use52WeekHigh = strategySection.GetValue<bool?>("Use52WeekHigh")
                ?? false
        };

        if (strategyConfiguration.MaxPositions <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{activeStrategyId}:MaxPositions' must be greater than zero.");
        }

        if (strategyConfiguration.MaxPositionWeight <= 0m ||
            strategyConfiguration.MaxPositionWeight > 100m)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{activeStrategyId}:MaxPositionWeight' must be greater than zero and less than or equal to 100.");
        }

        if (strategyConfiguration.LookbackDays <= 0)
        {
            throw new InvalidOperationException(
                $"Configuration value 'Strategies:{activeStrategyId}:LookbackDays' must be greater than zero.");
        }

        return strategyConfiguration;
    }
}
