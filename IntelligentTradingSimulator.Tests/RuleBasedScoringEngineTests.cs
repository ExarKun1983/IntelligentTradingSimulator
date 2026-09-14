using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class RuleBasedScoringEngineTests
{
    private static ScoringContext Context(
        decimal momentum,
        decimal volumeTrend,
        decimal volatility,
        bool isAboveSma5) =>
        new()
        {
            Symbol = "TEST",
            MomentumPercent = momentum,
            AverageVolume = 1_000_000m,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5
        };

    [Fact]
    public void CalculateScore_UsesExistingFormulaWithBonus_WhenAboveSma5()
    {
        var engine = new RuleBasedScoringEngine();

        var score = engine.CalculateScore(
            Context(momentum: 2m, volumeTrend: 20m, volatility: 1m, isAboveSma5: true));

        var expected = 2m * 10m + 20m * 0.05m - 1m + 5m;

        Assert.Equal(expected, score);
    }

    [Fact]
    public void CalculateScore_ReturnsDisqualifiedScore_WhenNotAboveSma5()
    {
        var engine = new RuleBasedScoringEngine();

        var score = engine.CalculateScore(
            Context(momentum: 10m, volumeTrend: 50m, volatility: 1m, isAboveSma5: false));

        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
    }

    [Fact]
    public void CalculateScore_ReturnsDisqualifiedScore_WhenQualityFilterEnabledAndVolatilityTooHigh()
    {
        var engine = new RuleBasedScoringEngine(
            new StrategyConfiguration { UseQualityFilter = true });

        var score = engine.CalculateScore(
            Context(
                momentum: 10m,
                volumeTrend: 50m,
                volatility: StrategyConfiguration.QualityVolatilityThresholdPercent + 0.1m,
                isAboveSma5: true));

        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
    }

    [Fact]
    public void CalculateScore_KeepsScore_WhenQualityFilterDisabledAndVolatilityHigh()
    {
        var engine = new RuleBasedScoringEngine(
            new StrategyConfiguration { UseQualityFilter = false });

        var volatility = StrategyConfiguration.QualityVolatilityThresholdPercent + 2m;

        var score = engine.CalculateScore(
            Context(momentum: 10m, volumeTrend: 50m, volatility: volatility, isAboveSma5: true));

        Assert.Equal(10m * 10m + 50m * 0.05m - volatility + 5m, score);
    }

    [Fact]
    public void CalculateScore_KeepsScore_WhenQualityFilterEnabledAndVolatilityAtThreshold()
    {
        var engine = new RuleBasedScoringEngine(
            new StrategyConfiguration { UseQualityFilter = true });

        var volatility = StrategyConfiguration.QualityVolatilityThresholdPercent;

        var score = engine.CalculateScore(
            Context(momentum: 1m, volumeTrend: 0m, volatility: volatility, isAboveSma5: true));

        Assert.Equal(1m * 10m - volatility + 5m, score);
    }

    [Fact]
    public void CalculateScore_IsUsableThroughInterface()
    {
        IScoringEngine engine = new RuleBasedScoringEngine();

        var score = engine.CalculateScore(
            Context(momentum: 0m, volumeTrend: 0m, volatility: 0m, isAboveSma5: true));

        Assert.Equal(5m, score);
    }
}
