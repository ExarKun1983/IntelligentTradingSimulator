using System.Collections.Generic;
using System.IO;
using System.Text;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyConfigurationLoaderTests
{
    [Fact]
    public void LoadAndLoadAll_BindQualityPolicyAndThresholds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseQualityFilter"] = "true",
                ["Strategies:default:QualityMinimumRevenueGrowthPercent"] = "-5",
                ["Strategies:default:QualityMinimumEpsGrowthPercent"] = "3",
                ["Strategies:default:QualityMinimumMarginsPercent"] = "10",
                ["Strategies:default:QualityMaximumDebtToEquity"] = "1.5",
                ["Strategies:default:QualityFailureAction"] = "Penalize",
                ["Strategies:default:QualityMissingDataAction"] = "Exclude",
                ["Strategies:default:QualityPenalty"] = "7"
            }).Build();

        foreach (var strategy in new[] { StrategyConfigurationLoader.Load(configuration),
                     Assert.Single(StrategyConfigurationLoader.LoadAll(configuration)) })
        {
            Assert.True(strategy.UseQualityFilter);
            Assert.Equal(-5m, strategy.QualityMinimumRevenueGrowthPercent);
            Assert.Equal(3m, strategy.QualityMinimumEpsGrowthPercent);
            Assert.Equal(10m, strategy.QualityMinimumMarginsPercent);
            Assert.Equal(1.5m, strategy.QualityMaximumDebtToEquity);
            Assert.Equal(QualityFilterAction.Penalize, strategy.QualityFailureAction);
            Assert.Equal(QualityFilterAction.Exclude, strategy.QualityMissingDataAction);
            Assert.Equal(7m, strategy.QualityPenalty);
        }
    }

    [Fact]
    public void Load_QualityDefaultsPreserveExistingBehavior()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Strategies:default:MaxPositions"] = "10" }).Build();
        var strategy = StrategyConfigurationLoader.Load(configuration);
        Assert.False(strategy.UseQualityFilter);
        Assert.Null(strategy.QualityMinimumRevenueGrowthPercent);
        Assert.Null(strategy.QualityMinimumEpsGrowthPercent);
        Assert.Null(strategy.QualityMinimumMarginsPercent);
        Assert.Null(strategy.QualityMaximumDebtToEquity);
        Assert.Equal(QualityFilterAction.Exclude, strategy.QualityFailureAction);
        Assert.Equal(QualityFilterAction.Neutral, strategy.QualityMissingDataAction);
        Assert.Equal(5m, strategy.QualityPenalty);
    }

    [Theory]
    [InlineData("QualityPenalty", "-1", "QualityMaximumDebtToEquity")]
    [InlineData("QualityMaximumDebtToEquity", "-1", "QualityPenalty")]
    [InlineData("QualityFailureAction", "99", null)]
    [InlineData("QualityMissingDataAction", "99", null)]
    [InlineData("QualityFailureAction", "invalid", null)]
    [InlineData("QualityMinimumRevenueGrowthPercent", "NaN", null)]
    [InlineData("QualityMinimumEpsGrowthPercent", "Infinity", null)]
    [InlineData("QualityMinimumMarginsPercent", "invalid", null)]
    public void LoadAndLoadAll_RejectInvalidQualityConfiguration(string name, string value, string? unrelatedName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"Strategies:default:{name}"] = value }).Build();
        var loadError = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration)).Message;
        var loadAllError = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.LoadAll(configuration)).Message;
        Assert.Contains($"Strategies:default:{name}", loadError);
        if (unrelatedName is not null)
            Assert.DoesNotContain($"Strategies:default:{unrelatedName}", loadError);
        Assert.Contains($"Strategies:default:{name}", loadAllError);
        if (unrelatedName is not null)
            Assert.DoesNotContain($"Strategies:default:{unrelatedName}", loadAllError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("10")]
    [InlineData("2.5")]
    [InlineData("100")]
    public void LoadAndLoadAll_BindOptionalStopLossPercent(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:MaxPositions"] = "10",
                ["Strategies:default:StopLossPercent"] = value
            }).Build();

        var expected = value is null ? (decimal?)null :
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, StrategyConfigurationLoader.Load(configuration).StopLossPercent);
        Assert.Equal(expected, Assert.Single(StrategyConfigurationLoader.LoadAll(configuration)).StopLossPercent);
        Assert.Null(new StrategyConfiguration().StopLossPercent);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("100.01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void LoadAndLoadAll_RejectInvalidStopLossPercent(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Strategies:default:StopLossPercent"] = value }).Build();

        Assert.Contains("Strategies:default:StopLossPercent", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains("Strategies:default:StopLossPercent", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Theory]
    [InlineData(null, 5, 1)]
    [InlineData("0", 0, 0)]
    [InlineData("2.5", 2.5, 2.5)]
    public void LoadAndLoadAll_BindSmaAndRsiWeights(string? weight, double sma, double rsi)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRsi"] = "true",
                ["Strategies:default:SmaScoreWeight"] = weight,
                ["Strategies:default:RsiScoreWeight"] = weight
            }).Build();

        foreach (var strategy in new[]
        {
            StrategyConfigurationLoader.Load(configuration),
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration))
        })
        {
            Assert.Equal((decimal)sma, strategy.SmaScoreWeight);
            Assert.Equal((decimal)rsi, strategy.RsiScoreWeight);
        }
    }

    [Theory]
    [InlineData("SmaScoreWeight", "-0.1")]
    [InlineData("RsiScoreWeight", "-0.1")]
    [InlineData("RsiScoreWeight", "NaN")]
    [InlineData("RelativeStrengthScoreWeight", "Infinity")]
    [InlineData("Week52HighScoreWeight", "not-a-weight")]
    public void LoadAndLoadAll_RejectInvalidWeights(string name, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"Strategies:default:{name}"] = value }).Build();

        Assert.Contains($"Strategies:default:{name}", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains($"Strategies:default:{name}", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void LoadAndLoadAll_RejectUnsupportedBenchmarkEvenWhenDisabled(string enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRelativeStrength"] = enabled,
                ["Strategies:default:RelativeStrengthBenchmarkSymbol"] = "UNKNOWN"
            }).Build();

        foreach (var exception in new[]
        {
            Assert.Throws<InvalidOperationException>(() => StrategyConfigurationLoader.Load(configuration)),
            Assert.Throws<InvalidOperationException>(() => StrategyConfigurationLoader.LoadAll(configuration))
        })
        {
            Assert.Contains("Strategies:default:RelativeStrengthBenchmarkSymbol", exception.Message);
            Assert.Contains("SPY, QQQ, IWM", exception.Message);
        }
    }

    [Theory]
    [InlineData(false, false, false, "1", "5", "1", "1", "5", false)]
    [InlineData(true, true, true, "0", "5", "0", "0", "0", false)]
    [InlineData(true, false, false, "1", "0", "0", "0", "0", false)]
    [InlineData(true, false, false, "1", "5", "0", "0", "0", true)]
    [InlineData(true, false, false, "1", "0", "5", "0", "0", true)]
    [InlineData(false, true, false, "0", "0", "0", "1", "0", true)]
    [InlineData(false, false, true, "0", "0", "0", "0", "5", true)]
    public void LoadAndLoadAll_RequireAnEffectiveActiveScoringFactor(
        bool useRsi, bool useRelativeStrength, bool use52WeekHigh,
        string rsiWeight, string bonus, string penalty, string relativeWeight, string week52Weight,
        bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:MomentumScoreWeight"] = "0",
                ["Strategies:default:VolumeTrendScoreWeight"] = "0",
                ["Strategies:default:VolatilityScoreWeight"] = "0",
                ["Strategies:default:SmaScoreWeight"] = "0",
                ["Strategies:default:UseQualityFilter"] = "true",
                ["Strategies:default:UseRsi"] = useRsi.ToString(),
                ["Strategies:default:RsiScoreWeight"] = rsiWeight,
                ["Strategies:default:RsiPreferredRangeBonus"] = bonus,
                ["Strategies:default:RsiOverboughtPenalty"] = penalty,
                ["Strategies:default:UseRelativeStrength"] = useRelativeStrength.ToString(),
                ["Strategies:default:RelativeStrengthScoreWeight"] = relativeWeight,
                ["Strategies:default:Use52WeekHigh"] = use52WeekHigh.ToString(),
                ["Strategies:default:Week52HighScoreWeight"] = week52Weight
            }).Build();

        if (valid)
        {
            Assert.NotNull(StrategyConfigurationLoader.Load(configuration));
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration));
        }
        else
        {
            Assert.Contains("Strategies:default", Assert.Throws<InvalidOperationException>(() =>
                StrategyConfigurationLoader.Load(configuration)).Message);
            Assert.Contains("at least one active scoring factor", Assert.Throws<InvalidOperationException>(() =>
                StrategyConfigurationLoader.LoadAll(configuration)).Message);
        }
    }

    [Fact]
    public void LoadAll_JsonVariantsApplyIndependentFactorSetupsAndWeights()
    {
        const string json = """
            {
              "ActiveStrategy": "advanced",
              "Strategies": {
                "baseline": {
                  "MomentumScoreWeight": 2, "VolumeTrendScoreWeight": 0.1,
                  "VolatilityScoreWeight": 3, "SmaScoreWeight": 4,
                  "UseRsi": false, "UseRelativeStrength": false, "Use52WeekHigh": false,
                  "RelativeStrengthBenchmarkSymbol": "SPY"
                },
                "advanced": {
                  "MomentumScoreWeight": 2, "VolumeTrendScoreWeight": 0.1,
                  "VolatilityScoreWeight": 3, "SmaScoreWeight": 4,
                  "UseRsi": true, "RsiScoreWeight": 2,
                  "UseRelativeStrength": true, "RelativeStrengthScoreWeight": 3,
                  "Use52WeekHigh": true, "Week52HighScoreWeight": 8,
                  "RelativeStrengthBenchmarkSymbol": " qqq "
                }
              }
            }
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        var strategies = StrategyConfigurationLoader.LoadAll(configuration);
        var baseline = Assert.Single(strategies, strategy => strategy.Id == "baseline");
        var advanced = Assert.Single(strategies, strategy => strategy.Id == "advanced");
        var context = new ScoringContext
        {
            IsAboveSma5 = true, MomentumPercent = 2m, VolumeTrendPercent = 20m,
            VolatilityPercent = 1m, Rsi = 60m, RelativeStrengthPercent = 3m,
            DistanceFromWeek52HighPercent = -25m
        };

        Assert.Equal("SPY", baseline.RelativeStrengthBenchmarkSymbol);
        Assert.Equal("QQQ", advanced.RelativeStrengthBenchmarkSymbol);
        Assert.Equal("advanced", StrategyConfigurationLoader.Load(configuration).Id);
        Assert.Equal(7m, new CompositeScoringEngine(baseline).CalculateScore(context));
        var result = new CompositeScoringEngine(advanced).CalculateContributions(context);
        Assert.Equal(10m, result.RsiContribution);
        Assert.Equal(9m, result.RelativeStrengthContribution);
        Assert.Equal(4m, result.Week52HighContribution);
        Assert.Equal(30m, result.Score);
        Assert.Equal(result.Score, new RuleBasedScoringEngine(advanced).CalculateScore(context));
        advanced.RsiScoreWeight = 0m;
        Assert.Equal(1m, baseline.RsiScoreWeight);
        Assert.Equal(7m, new CompositeScoringEngine(baseline).CalculateScore(context));
    }

    [Theory]
    [InlineData(null, 10, 0.05, 1)]
    [InlineData("0", 0, 0, 0)]
    [InlineData("2.5", 2.5, 2.5, 2.5)]
    public void LoadAndLoadAll_BindCompositeBaseWeights(
        string? weight, double momentum, double volume, double volatility)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRsi"] = "false",
                ["Strategies:default:MomentumScoreWeight"] = weight,
                ["Strategies:default:VolumeTrendScoreWeight"] = weight,
                ["Strategies:default:VolatilityScoreWeight"] = weight
            }).Build();

        foreach (var strategy in new[]
        {
            StrategyConfigurationLoader.Load(configuration),
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration))
        })
        {
            Assert.Equal((decimal)momentum, strategy.MomentumScoreWeight);
            Assert.Equal((decimal)volume, strategy.VolumeTrendScoreWeight);
            Assert.Equal((decimal)volatility, strategy.VolatilityScoreWeight);
        }
    }

    [Theory]
    [InlineData("MomentumScoreWeight")]
    [InlineData("VolumeTrendScoreWeight")]
    [InlineData("VolatilityScoreWeight")]
    public void LoadAndLoadAll_RejectNegativeCompositeBaseWeights(string name)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [$"Strategies:default:{name}"] = "-0.1" }).Build();

        Assert.Contains($"Strategies:default:{name}", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains($"Strategies:default:{name}", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("0", 0)]
    [InlineData("2.5", 2.5)]
    public void LoadAndLoadAll_BindRelativeStrengthWeight(string? weight, double expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRelativeStrength"] = "true",
                ["Strategies:default:RelativeStrengthScoreWeight"] = weight
            }).Build();

        Assert.Equal((decimal)expected,
            StrategyConfigurationLoader.Load(configuration).RelativeStrengthScoreWeight);
        Assert.Equal((decimal)expected,
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration)).RelativeStrengthScoreWeight);
        Assert.Equal(StrategyConfiguration.DefaultRelativeStrengthScoreWeight,
            new StrategyConfiguration().RelativeStrengthScoreWeight);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void LoadAndLoadAll_RejectNegativeRelativeStrengthWeight_EvenWhenDisabled(string enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRelativeStrength"] = enabled,
                ["Strategies:default:RelativeStrengthScoreWeight"] = "-0.1"
            }).Build();

        Assert.Contains("Strategies:default:RelativeStrengthScoreWeight",
            Assert.Throws<InvalidOperationException>(() =>
                StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains("Strategies:default:RelativeStrengthScoreWeight",
            Assert.Throws<InvalidOperationException>(() =>
                StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Theory]
    [InlineData(null, "SPY")]
    [InlineData(" QQQ ", "QQQ")]
    [InlineData("IWM", "IWM")]
    public void Load_BindsRelativeStrengthBenchmark(string? symbol, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:UseRelativeStrength"] = "true",
                ["Strategies:default:RelativeStrengthBenchmarkSymbol"] = symbol
            }).Build();

        Assert.Equal(expected,
            StrategyConfigurationLoader.Load(configuration).RelativeStrengthBenchmarkSymbol);
        Assert.Equal(expected,
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration)).RelativeStrengthBenchmarkSymbol);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Load_RejectsBlankRelativeStrengthBenchmark(string symbol)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:RelativeStrengthBenchmarkSymbol"] = symbol
            }).Build();

        Assert.Contains("RelativeStrengthBenchmarkSymbol",
            Assert.Throws<InvalidOperationException>(() =>
                StrategyConfigurationLoader.Load(configuration)).Message);
    }

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
                        [$"Strategies:{strategyId}:UseHoldingConditionExit"] = "false",
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
        Assert.False(result.UseHoldingConditionExit);
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
        Assert.True(result.UseHoldingConditionExit);
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

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void LoadAll_AcceptsStrategyComparisonLimitWithinRange(int strategyCount)
    {
        var configurationValues = new Dictionary<string, string?>();
        for (var index = 0; index < strategyCount; index++)
        {
            configurationValues[$"Strategies:strategy-{index}:MaxPositions"] = "5";
        }

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

        var results = StrategyConfigurationLoader.LoadAll(configuration);

        Assert.Equal(strategyCount, results.Count);
    }

    [Fact]
    public void LoadAll_Throws_WhenStrategyComparisonLimitIsExceeded()
    {
        var configurationValues = new Dictionary<string, string?>();
        for (var index = 0; index < 101; index++)
        {
            configurationValues[$"Strategies:strategy-{index}:MaxPositions"] = "5";
        }

        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(configurationValues)
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.LoadAll(configuration));

        Assert.Equal(
            "Configuration section 'Strategies' contains 101 strategy definitions; the maximum strategy comparison limit is 100.",
            exception.Message);
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
