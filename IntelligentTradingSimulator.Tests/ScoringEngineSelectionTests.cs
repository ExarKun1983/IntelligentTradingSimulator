using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class ScoringEngineSelectionTests
{
    [Theory]
    [InlineData(null, typeof(CompositeScoringEngine))]
    [InlineData("Composite", typeof(CompositeScoringEngine))]
    [InlineData("RuleBased", typeof(RuleBasedScoringEngine))]
    [InlineData("AI", typeof(AiScoringEngine))]
    [InlineData("ai", typeof(AiScoringEngine))]
    public void Configuration_SelectsEngine_ForRankingAndBacktestAnalyzers(string? name, Type expectedType)
    {
        var configuration = Configuration(name);
        foreach (var strategy in new[]
        {
            StrategyConfigurationLoader.Load(configuration),
            Assert.Single(StrategyConfigurationLoader.LoadAll(configuration))
        })
        {
            var engine = ScoringEngineFactory.Create(strategy);
            Assert.IsType(expectedType, engine);
            var candles = Enumerable.Range(0, 5).Select(index => new MarketCandle
            {
                Date = new DateTime(2024, 1, 1).AddDays(index),
                Close = 100m + index,
                High = 101m + index,
                Low = 99m + index,
                Volume = 1_000_000m
            }).ToArray();
            var ranking = new MarketObservationAnalyzer(engine).Analyze("TEST", candles);
            var backtest = new MarketObservationAnalyzerFactory().Create(strategy).Analyze("TEST", candles);
            var direct = new MarketObservationAnalyzer(strategy).Analyze("TEST", candles);

            Assert.Equal(4m, ranking.MomentumPercent);
            Assert.Equal(8m, ranking.Score);
            Assert.Equal(ranking.Score, backtest.Score);
            Assert.Equal(ranking.FactorContributions, backtest.FactorContributions);
            Assert.Equal(ranking.Score, direct.Score);
        }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("99")]
    public void Configuration_RejectsUnsupportedEngine(string name)
    {
        var configuration = Configuration(name);

        Assert.Contains("ScoringEngine",
            Assert.Throws<InvalidOperationException>(() => StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains("ScoringEngine",
            Assert.Throws<InvalidOperationException>(() => StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Fact]
    public void Factory_RejectsInvalidProgrammaticConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => ScoringEngineFactory.Create(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScoringEngineFactory.Create(
            new StrategyConfiguration { ScoringEngine = (ScoringEngineType)99 }));
    }

    private static IConfiguration Configuration(string? name)
    {
        var values = new Dictionary<string, string?>
        {
            ["Strategies:default:MomentumScoreWeight"] = "2",
            ["Strategies:default:VolatilityScoreWeight"] = "0",
            ["Strategies:default:SmaScoreWeight"] = "0",
            ["Strategies:default:UseRelativeStrength"] = "false"
        };
        if (name is not null)
            values["Strategies:default:ScoringEngine"] = name;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
