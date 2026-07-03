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
    public void Load_BindsStrategyFlags_WhenExplicitlyConfigured()
    {
        const string strategyId = "flags-strategy";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = strategyId,
                        [$"Strategies:{strategyId}:LookbackDays"] = "60",
                        [$"Strategies:{strategyId}:UseRsi"] = "true",
                        [$"Strategies:{strategyId}:UseRelativeStrength"] = "false",
                        [$"Strategies:{strategyId}:Use52WeekHigh"] = "true",
                        [$"Strategies:{strategyId}:UseQualityFilter"] = "true"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(60, result.LookbackDays);
        Assert.True(result.UseRsi);
        Assert.False(result.UseRelativeStrength);
        Assert.True(result.Use52WeekHigh);
        Assert.True(result.UseQualityFilter);
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
        Assert.Equal(StrategyConfiguration.DefaultLookbackDays, result.LookbackDays);
        Assert.False(result.UseRsi);
        Assert.True(result.UseRelativeStrength);
        Assert.False(result.Use52WeekHigh);
        Assert.False(result.UseQualityFilter);
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

    // ── LoadAll tests ──────────────────────────────────────────────────────

    [Fact]
    public void LoadAll_ReturnsAllDefinedVariants()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategies:alpha:MaxPositions"] = "5",
                        ["Strategies:alpha:MaxPositionWeight"] = "30",
                        ["Strategies:beta:MaxPositions"] = "8",
                        ["Strategies:beta:MaxPositionWeight"] = "20"
                    })
                .Build();

        var results = StrategyConfigurationLoader.LoadAll(configuration);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "alpha" && r.MaxPositions == 5);
        Assert.Contains(results, r => r.Id == "beta" && r.MaxPositions == 8);
    }

    [Fact]
    public void LoadAll_BindsAllFieldsForEachVariant()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategies:v1:MaxPositions"] = "6",
                        ["Strategies:v1:MaxPositionWeight"] = "40",
                        ["Strategies:v1:LookbackDays"] = "120",
                        ["Strategies:v1:UseRsi"] = "true",
                        ["Strategies:v1:UseRelativeStrength"] = "false",
                        ["Strategies:v1:Use52WeekHigh"] = "true",
                        ["Strategies:v1:UseQualityFilter"] = "true"
                    })
                .Build();

        var results = StrategyConfigurationLoader.LoadAll(configuration);

        Assert.Single(results);
        var v1 = results[0];
        Assert.Equal("v1", v1.Id);
        Assert.Equal(6, v1.MaxPositions);
        Assert.Equal(40m, v1.MaxPositionWeight);
        Assert.Equal(120, v1.LookbackDays);
        Assert.True(v1.UseRsi);
        Assert.False(v1.UseRelativeStrength);
        Assert.True(v1.Use52WeekHigh);
        Assert.True(v1.UseQualityFilter);
    }

    [Fact]
    public void LoadAll_UsesDefaultsWhenOptionalFieldsMissing()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategies:minimal:MaxPositions"] = "5",
                        ["Strategies:minimal:MaxPositionWeight"] = "25"
                    })
                .Build();

        var results = StrategyConfigurationLoader.LoadAll(configuration);

        Assert.Single(results);
        var minimal = results[0];
        Assert.Equal(StrategyConfiguration.DefaultLookbackDays, minimal.LookbackDays);
        Assert.False(minimal.UseRsi);
        Assert.True(minimal.UseRelativeStrength);
        Assert.False(minimal.Use52WeekHigh);
        Assert.False(minimal.UseQualityFilter);
    }

    [Fact]
    public void LoadAll_Throws_WhenStrategiesSectionMissing()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.LoadAll(configuration));

        Assert.Equal(
            "Configuration section 'Strategies' is missing or empty.",
            exception.Message);
    }

    [Fact]
    public void LoadAll_Throws_WhenOneVariantHasInvalidMaxPositions()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategies:bad:MaxPositions"] = "0",
                        ["Strategies:bad:MaxPositionWeight"] = "25"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.LoadAll(configuration));

        Assert.Contains("MaxPositions", exception.Message);
    }

    [Fact]
    public void LoadAll_ReadsVariants_FromJsonShape()
    {
        const string json = """
                            {
                              "Strategies": {
                                "conservative": {
                                  "MaxPositions": 5,
                                  "MaxPositionWeight": 25
                                },
                                "aggressive": {
                                  "MaxPositions": 15,
                                  "MaxPositionWeight": 10
                                }
                              }
                            }
                            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var results = StrategyConfigurationLoader.LoadAll(configuration);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "conservative" && r.MaxPositions == 5);
        Assert.Contains(results, r => r.Id == "aggressive" && r.MaxPositions == 15);
    }
}
