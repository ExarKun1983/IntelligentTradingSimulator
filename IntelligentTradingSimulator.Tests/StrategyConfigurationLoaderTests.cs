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
                        [$"Strategies:{strategyId}:RsiPreferredMinimum"] = "45",
                        [$"Strategies:{strategyId}:RsiPreferredMaximum"] = "68",
                        [$"Strategies:{strategyId}:RsiOverboughtThreshold"] = "78",
                        [$"Strategies:{strategyId}:RsiPreferredRangeBonus"] = "2.5",
                        [$"Strategies:{strategyId}:RsiOverboughtPenalty"] = "3.5",
                        [$"Strategies:{strategyId}:UseRelativeStrength"] = "false",
                        [$"Strategies:{strategyId}:Use52WeekHigh"] = "true",
                        [$"Strategies:{strategyId}:Week52HighScoreWeight"] = "4.5",
                        [$"Strategies:{strategyId}:UseQualityFilter"] = "true"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(60, result.LookbackDays);
        Assert.True(result.UseRsi);
        Assert.Equal(45m, result.RsiPreferredMinimum);
        Assert.Equal(68m, result.RsiPreferredMaximum);
        Assert.Equal(78m, result.RsiOverboughtThreshold);
        Assert.Equal(2.5m, result.RsiPreferredRangeBonus);
        Assert.Equal(3.5m, result.RsiOverboughtPenalty);
        Assert.False(result.UseRelativeStrength);
        Assert.True(result.Use52WeekHigh);
        Assert.Equal(4.5m, result.Week52HighScoreWeight);
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
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredMinimum, result.RsiPreferredMinimum);
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredMaximum, result.RsiPreferredMaximum);
        Assert.Equal(StrategyConfiguration.DefaultRsiOverboughtThreshold, result.RsiOverboughtThreshold);
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredRangeBonus, result.RsiPreferredRangeBonus);
        Assert.Equal(StrategyConfiguration.DefaultRsiOverboughtPenalty, result.RsiOverboughtPenalty);
        Assert.True(result.UseRelativeStrength);
        Assert.False(result.Use52WeekHigh);
        Assert.Equal(
            StrategyConfiguration.DefaultWeek52HighScoreWeight,
            result.Week52HighScoreWeight);
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
    [InlineData(1)]
    [InlineData(100)]
    public void Load_AcceptsMaxPositionsWithinStrategyComparisonLimit(int maxPositions)
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

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal(maxPositions, result.MaxPositions);
    }

    [Fact]
    public void Load_Throws_WhenMaxPositionsExceedsStrategyComparisonLimit()
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:MaxPositions"] = "101"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategies:momentum-v1:MaxPositions' must be less than or equal to 100.",
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

    [Theory]
    [InlineData("RsiPreferredMinimum", "-1")]
    [InlineData("RsiPreferredMinimum", "100.01")]
    [InlineData("RsiPreferredMaximum", "-1")]
    [InlineData("RsiPreferredMaximum", "100.01")]
    [InlineData("RsiOverboughtThreshold", "-1")]
    [InlineData("RsiOverboughtThreshold", "100.01")]
    public void Load_Throws_WhenRsiLevelIsOutsideZeroToHundred(string key, string value)
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:{key}"] = value
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Contains($"Strategies:{activeStrategyId}:{key}", exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenRsiPreferredMinimumExceedsMaximum()
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:RsiPreferredMinimum"] = "71",
                        [$"Strategies:{activeStrategyId}:RsiPreferredMaximum"] = "70"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Contains("RsiPreferredMinimum", exception.Message);
        Assert.Contains("RsiPreferredMaximum", exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenRsiOverboughtThresholdIsBelowPreferredMaximum()
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:RsiPreferredMaximum"] = "70",
                        [$"Strategies:{activeStrategyId}:RsiOverboughtThreshold"] = "69.99"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Contains("RsiOverboughtThreshold", exception.Message);
        Assert.Contains("RsiPreferredMaximum", exception.Message);
    }

    [Theory]
    [InlineData("RsiPreferredRangeBonus")]
    [InlineData("RsiOverboughtPenalty")]
    public void Load_Throws_WhenRsiWeightIsNegative(string key)
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:{key}"] = "-0.01"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Contains($"Strategies:{activeStrategyId}:{key}", exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenWeek52HighScoreWeightIsNegative()
    {
        const string activeStrategyId = "momentum-v1";

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = activeStrategyId,
                        [$"Strategies:{activeStrategyId}:Week52HighScoreWeight"] = "-0.01"
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategies:momentum-v1:Week52HighScoreWeight' must be greater than or equal to zero.",
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
                        ["Strategies:v1:RsiPreferredMinimum"] = "48",
                        ["Strategies:v1:RsiPreferredMaximum"] = "69",
                        ["Strategies:v1:RsiOverboughtThreshold"] = "82",
                        ["Strategies:v1:RsiPreferredRangeBonus"] = "1.5",
                        ["Strategies:v1:RsiOverboughtPenalty"] = "2.25",
                        ["Strategies:v1:UseRelativeStrength"] = "false",
                        ["Strategies:v1:Use52WeekHigh"] = "true",
                        ["Strategies:v1:Week52HighScoreWeight"] = "3.75",
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
        Assert.Equal(48m, v1.RsiPreferredMinimum);
        Assert.Equal(69m, v1.RsiPreferredMaximum);
        Assert.Equal(82m, v1.RsiOverboughtThreshold);
        Assert.Equal(1.5m, v1.RsiPreferredRangeBonus);
        Assert.Equal(2.25m, v1.RsiOverboughtPenalty);
        Assert.False(v1.UseRelativeStrength);
        Assert.True(v1.Use52WeekHigh);
        Assert.Equal(3.75m, v1.Week52HighScoreWeight);
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
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredMinimum, minimal.RsiPreferredMinimum);
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredMaximum, minimal.RsiPreferredMaximum);
        Assert.Equal(StrategyConfiguration.DefaultRsiOverboughtThreshold, minimal.RsiOverboughtThreshold);
        Assert.Equal(StrategyConfiguration.DefaultRsiPreferredRangeBonus, minimal.RsiPreferredRangeBonus);
        Assert.Equal(StrategyConfiguration.DefaultRsiOverboughtPenalty, minimal.RsiOverboughtPenalty);
        Assert.True(minimal.UseRelativeStrength);
        Assert.False(minimal.Use52WeekHigh);
        Assert.Equal(
            StrategyConfiguration.DefaultWeek52HighScoreWeight,
            minimal.Week52HighScoreWeight);
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
