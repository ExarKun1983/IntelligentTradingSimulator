using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class CompositeScoringEngineTests
{
    private static ScoringContext Context(
        decimal momentum = 2m, decimal volume = 20m, decimal volatility = 1m,
        decimal? rsi = 60m, decimal? distance = -25m, decimal? relativeStrength = 3m,
        bool aboveSma = true) => new()
        {
            MomentumPercent = momentum,
            VolumeTrendPercent = volume,
            VolatilityPercent = volatility,
            Rsi = rsi,
            DistanceFromWeek52HighPercent = distance,
            RelativeStrengthPercent = relativeStrength,
            IsAboveSma5 = aboveSma
        };

    [Fact]
    public void CalculateContributions_AggregatesAllFactorsWithCustomWeights()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            MomentumScoreWeight = 2m,
            VolumeTrendScoreWeight = 0.1m,
            VolatilityScoreWeight = 3m,
            UseRsi = true,
            RsiPreferredRangeBonus = 4m,
            Use52WeekHigh = true,
            Week52HighScoreWeight = 8m,
            RelativeStrengthScoreWeight = 2m
        });

        var result = engine.CalculateContributions(Context());

        Assert.Equal(4m, result.MomentumContribution);
        Assert.Equal(2m, result.VolumeTrendContribution);
        Assert.Equal(-3m, result.VolatilityContribution);
        Assert.Equal(5m, result.SmaContribution);
        Assert.Equal(4m, result.RsiContribution);
        Assert.Equal(4m, result.Week52HighContribution);
        Assert.Equal(6m, result.RelativeStrengthContribution);
        Assert.False(result.IsDisqualified);
        Assert.Equal(22m, result.Score);
        Assert.Equal(result.Score, engine.CalculateScore(Context(), out var relativeContribution));
        Assert.Equal(result.RelativeStrengthContribution, relativeContribution);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(result.Score, engine.CalculateContributions(Context()).Score);
        }
    }

    [Theory]
    [InlineData(2, 20, 1, 3, 28)]
    [InlineData(-2, -20, 1, -3, -20)]
    [InlineData(0, 0, 0, 0, 5)]
    [InlineData(2, 20, 3, 3, 26)]
    public void CalculateScore_PreservesDefaultUnitsAndDirections(
        int momentum, int volume, int volatility, int relativeStrength, int expected)
    {
        Assert.Equal(expected, new CompositeScoringEngine().CalculateScore(
            Context(momentum, volume, volatility, relativeStrength: relativeStrength)));
    }

    [Fact]
    public void CalculateContributions_ZeroWeightsDisableBaseFactors()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            MomentumScoreWeight = 0m,
            VolumeTrendScoreWeight = 0m,
            VolatilityScoreWeight = 0m,
            UseRelativeStrength = false
        });
        var result = engine.CalculateContributions(Context());

        Assert.Equal(0m, result.MomentumContribution);
        Assert.Equal(0m, result.VolumeTrendContribution);
        Assert.Equal(0m, result.VolatilityContribution);
        Assert.Equal(0m, result.RsiContribution);
        Assert.Equal(0m, result.Week52HighContribution);
        Assert.Equal(0m, result.RelativeStrengthContribution);
        Assert.Equal(5m, result.Score);
    }

    [Fact]
    public void CalculateContributions_MissingOptionalDataContributesZero()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration { UseRsi = true, Use52WeekHigh = true });
        var result = engine.CalculateContributions(Context(rsi: null, distance: null, relativeStrength: null));

        Assert.Equal(0m, result.RsiContribution);
        Assert.Equal(0m, result.Week52HighContribution);
        Assert.Equal(0m, result.RelativeStrengthContribution);
        Assert.Equal(25m, result.Score);
    }

    [Theory]
    [InlineData(10, 8)]
    [InlineData(0, 8)]
    [InlineData(-25, 4)]
    [InlineData(-50, 0)]
    [InlineData(-75, -4)]
    [InlineData(-100, -8)]
    [InlineData(-150, -8)]
    public void CalculateContributions_NormalizesAndClampsWeek52Distance(int distance, int expected)
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration { Use52WeekHigh = true, Week52HighScoreWeight = 8m });
        Assert.Equal(expected, engine.CalculateContributions(Context(distance: distance)).Week52HighContribution);
    }

    [Theory]
    [InlineData(49, 0)]
    [InlineData(50, 4)]
    [InlineData(70, 4)]
    [InlineData(71, 0)]
    [InlineData(80, -7)]
    [InlineData(100, -7)]
    public void CalculateContributions_UsesConfiguredRsiThresholdPoints(int rsi, int expected)
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            UseRsi = true, RsiPreferredRangeBonus = 4m, RsiOverboughtPenalty = 7m
        });
        Assert.Equal(expected, engine.CalculateContributions(Context(rsi: rsi)).RsiContribution);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 4)]
    public void CalculateContributions_DisqualificationClearsAllAppliedContributions(
        bool aboveSma, bool qualityFilter, int volatility)
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            UseQualityFilter = qualityFilter, UseRsi = true, Use52WeekHigh = true,
            VolatilityScoreWeight = 0m
        });
        var result = engine.CalculateContributions(Context(volatility: volatility, aboveSma: aboveSma));

        Assert.True(result.IsDisqualified);
        Assert.Equal(StrategyConfiguration.DisqualifiedScore, result.Score);
        Assert.Equal(0m, result.MomentumContribution + result.VolumeTrendContribution +
            result.VolatilityContribution + result.SmaContribution + result.RsiContribution +
            result.Week52HighContribution + result.RelativeStrengthContribution);
    }

    [Theory]
    [InlineData("MomentumScoreWeight")]
    [InlineData("VolumeTrendScoreWeight")]
    [InlineData("VolatilityScoreWeight")]
    public void CalculateScore_RejectsNegativeBaseWeights(string name)
    {
        var configuration = new StrategyConfiguration();
        typeof(StrategyConfiguration).GetProperty(name)!.SetValue(configuration, -1m);
        Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompositeScoringEngine(configuration).CalculateScore(Context())).ParamName);
    }

    [Fact]
    public void CalculateScore_RejectsNullAndSurfacesOverflow()
    {
        var engine = new CompositeScoringEngine();
        Assert.Throws<ArgumentNullException>(() => engine.CalculateScore(null!));
        Assert.Throws<OverflowException>(() => engine.CalculateScore(Context(momentum: decimal.MaxValue)));
    }

    [Fact]
    public void AnalyzerAndBacktestFactory_UseSameConfiguredCompositeScore()
    {
        var configuration = new StrategyConfiguration
        {
            MomentumScoreWeight = 2m, VolumeTrendScoreWeight = 0m,
            VolatilityScoreWeight = 0m, UseRelativeStrength = false
        };
        var candles = Enumerable.Range(0, 5).Select(index => new MarketCandle
        {
            Date = new DateTime(2026, 1, 1).AddDays(index),
            Close = 100m + index, High = 101m + index, Low = 99m + index, Volume = 1000m
        }).ToList();

        var rankingResult = new MarketObservationAnalyzer(configuration).Analyze("TEST", candles);
        var backtestResult = new MarketObservationAnalyzerFactory().Create(configuration).Analyze("TEST", candles);

        Assert.Equal(13m, rankingResult.Score);
        Assert.Equal(rankingResult.Score, backtestResult.Score);
        Assert.Equal(0m, backtestResult.RelativeStrengthScoreContribution);
    }
}
