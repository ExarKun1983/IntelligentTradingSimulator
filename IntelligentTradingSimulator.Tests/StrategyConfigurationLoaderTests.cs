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
    public void Load_BindsStrategyConfigurationFromAppSettingsValues()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategy:Id"] = "momentum-v1",
                        ["Strategy:MaxPositions"] = "7",
                        ["Strategy:MaxPositionWeight"] = "35"
                    })
                .Build();

        var result = StrategyConfigurationLoader.Load(configuration);

        Assert.Equal("momentum-v1", result.Id);
        Assert.Equal(7, result.MaxPositions);
        Assert.Equal(35m, result.MaxPositionWeight);
    }

    [Fact]
    public void Load_UsesStrategyConfigurationDefaults_WhenValuesMissing()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>())
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
                              "Strategy": {
                                "Id": "strategy-from-json",
                                "MaxPositions": 5,
                                "MaxPositionWeight": 45
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
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategy:MaxPositions"] = maxPositions.ToString()
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategy:MaxPositions' must be greater than zero.",
            exception.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.01")]
    [InlineData("100.01")]
    public void Load_Throws_WhenMaxPositionWeightIsInvalid(string maxPositionWeight)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategy:MaxPositionWeight"] = maxPositionWeight
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategy:MaxPositionWeight' must be greater than zero and less than or equal to 100.",
            exception.Message);
    }

    [Fact]
    public void Load_Throws_WhenStrategyIdIsEmpty()
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Strategy:Id"] = " "
                    })
                .Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => StrategyConfigurationLoader.Load(configuration));

        Assert.Equal(
            "Configuration value 'Strategy:Id' must not be empty.",
            exception.Message);
    }
}
