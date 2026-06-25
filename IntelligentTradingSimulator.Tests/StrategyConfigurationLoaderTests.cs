using System.Collections.Generic;
using System.IO;
using System.Text;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyConfigurationLoaderTests
{
    [Fact]
    public void Load_BindsSelectedNamedStrategyConfigurationFromAppSettingsValues()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = "momentum-v2",
                        ["Strategies:momentum-v1:MaxPositions"] = "7",
                        ["Strategies:momentum-v1:MaxPositionWeight"] = "35",
                        ["Strategies:momentum-v2:MaxPositions"] = "12",
                        ["Strategies:momentum-v2:MaxPositionWeight"] = "20"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal("momentum-v2", result.Id);
        Assert.Equal(12, result.MaxPositions);
        Assert.Equal(20m, result.MaxPositionWeight);
    }

    [Fact]
    public void Load_UsesStrategyConfigurationDefaults_WhenSelectedStrategyValuesMissing()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = "default",
                        ["Strategies:default:DisplayName"] = "Defaults Only"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(StrategyConfiguration.DefaultId, result.Id);
        Assert.Equal(StrategyConfiguration.DefaultMaxPositions, result.MaxPositions);
        Assert.Equal(StrategyConfiguration.DefaultMaxPositionWeight, result.MaxPositionWeight);
    }

    [Fact]
    public void Load_ReadsStrategyValues_FromAppsettingsJsonShape()
    {
        const string json = """
                            {
                             "ActiveStrategy": "strategy-from-json",
                             "Strategies": {
                               "strategy-from-json": {
                                 "MaxPositions": 5,
                                 "MaxPositionWeight": 45
                               }
                              }
                            }
                            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var configuration =
            new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal("strategy-from-json", result.Id);
        Assert.Equal(5, result.MaxPositions);
        Assert.Equal(45m, result.MaxPositionWeight);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Load_Throws_WhenMaxPositionsIsInvalid(int maxPositions)
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:MaxPositions"] = maxPositions.ToString()
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategies:momentum-v1:MaxPositions' must be greater than zero.",
            exception.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public void Load_Throws_WhenMaxPositionWeightIsInvalid(string maxPositionWeight)
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:MaxPositionWeight"] = maxPositionWeight
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategies:momentum-v1:MaxPositionWeight' must be greater than zero and less than or equal to 100.",
            exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenSelectedStrategyDefinitionIsMissing()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = "momentum-v1"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration section 'Strategies:momentum-v1' is missing for selected active strategy.",
            exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenActiveStrategyIsEmpty()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = " "
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'ActiveStrategy' must not be empty.",
            exception.Message);
    }

    [Fact]
    public void Load_BindsLookbackDays_FromConfiguration()
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:LookbackDays"] = "120"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(120, result.LookbackDays);
    }

    [Fact]
    public void Load_UsesDefaultLookbackDays_WhenNotConfigured()
    {
        const string activeStrategyId = "default";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:MaxPositions"] = "5"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(StrategyConfiguration.DefaultLookbackDays, result.LookbackDays);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Load_Throws_WhenLookbackDaysIsInvalid(int lookbackDays)
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:LookbackDays"] = lookbackDays.ToString()
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategies:momentum-v1:LookbackDays' must be greater than zero.",
            exception.Message);
    }

    [Fact]
    public void Load_BindsFactorFlags_FromConfiguration()
    {
        const string activeStrategyId = "factor-strategy";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:UseRsi"] = "true",
                        [$"Strategies:{activeStrategyId}:UseRelativeStrength"] = "false",
                        [$"Strategies:{activeStrategyId}:Use52WeekHigh"] = "true"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.True(result.UseRsi);
        Assert.False(result.UseRelativeStrength);
        Assert.True(result.Use52WeekHigh);
    }

    [Fact]
    public void Load_UsesFactorFlagDefaults_WhenNotConfigured()
    {
        const string activeStrategyId = "default";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:MaxPositions"] = "5"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.False(result.UseRsi);
        Assert.True(result.UseRelativeStrength);
        Assert.False(result.Use52WeekHigh);
    }

    [Fact]
    public void Load_ReadsNewFields_FromAppsettingsJsonShape()
    {
        const string json = """
                            {
                             "ActiveStrategy": "extended-strategy",
                             "Strategies": {
                               "extended-strategy": {
                                 "MaxPositions": 8,
                                 "MaxPositionWeight": 20,
                                 "LookbackDays": 60,
                                 "UseRsi": true,
                                 "UseRelativeStrength": true,
                                 "Use52WeekHigh": false
                               }
                              }
                            }
                            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var configuration =
            new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal("extended-strategy", result.Id);
        Assert.Equal(60, result.LookbackDays);
        Assert.True(result.UseRsi);
        Assert.True(result.UseRelativeStrength);
        Assert.False(result.Use52WeekHigh);
    }
}
