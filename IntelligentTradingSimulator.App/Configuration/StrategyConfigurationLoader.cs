using System;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyConfigurationLoader
{
    public static StrategyConfiguration Load(IConfiguration configuration)
    {
        var strategySection = configuration.GetSection("Strategy");

        var strategyConfiguration = new StrategyConfiguration
        {
            Id = strategySection.GetValue<string>("Id") ?? StrategyConfiguration.DefaultId,
            MaxPositions = strategySection.GetValue<int?>("MaxPositions")
                ?? StrategyConfiguration.DefaultMaxPositions,
            MaxPositionWeight = strategySection.GetValue<decimal?>("MaxPositionWeight")
                ?? StrategyConfiguration.DefaultMaxPositionWeight
        };

        if (string.IsNullOrWhiteSpace(strategyConfiguration.Id))
        {
            throw new InvalidOperationException(
                "Configuration value 'Strategy:Id' must not be empty.");
        }

        if (strategyConfiguration.MaxPositions <= 0)
        {
            throw new InvalidOperationException(
                "Configuration value 'Strategy:MaxPositions' must be greater than zero.");
        }

        if (strategyConfiguration.MaxPositionWeight <= 0m ||
            strategyConfiguration.MaxPositionWeight > 100m)
        {
            throw new InvalidOperationException(
                "Configuration value 'Strategy:MaxPositionWeight' must be greater than zero and less than or equal to 100.");
        }

        return strategyConfiguration;
    }
}
