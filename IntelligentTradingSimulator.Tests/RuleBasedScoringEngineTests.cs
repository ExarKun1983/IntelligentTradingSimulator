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
        bool isAboveSma5,
        decimal? rsi = null,
        decimal? distanceFromWeek52HighPercent = null,
        decimal? relativeStrengthPercent = null) =>
        new()
        {
            Symbol = "TEST",
            MomentumPercent = momentum,
            AverageVolume = 1_000_000m,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5,
            Rsi = rsi,
            DistanceFromWeek52HighPercent = distanceFromWeek52HighPercent,
            RelativeStrengthPercent = relativeStrengthPercent
        };

    [Theory]
    [InlineData(10d, 1, 10)]
    [InlineData(-10d, 1, -10)]
    [InlineData(0d, 1, 0)]
    [InlineData(10d, 2.5, 25)]
    [InlineData(-10d, 2.5, -25)]
    [InlineData(10d, 0, 0)]
    [InlineData(null, 2.5, 0)]
    public void CalculateScore_AppliesWeightedRelativeStrengthAndReportsContribution(
        double? relativeStrength, double weight, double expectedContribution)
    {
        var engine = new RuleBasedScoringEngine(new StrategyConfiguration
        {
            UseRelativeStrength = true,
            RelativeStrengthScoreWeight = (decimal)weight
        });
        var context = Context(2m, 20m, 1m, true,
            relativeStrengthPercent: (decimal?)relativeStrength);

        var score = engine.CalculateScore(context, out var contribution);

        Assert.Equal((decimal)expectedContribution, contribution);
        Assert.Equal(25m + (decimal)expectedContribution, score);
        Assert.Equal(score, engine.CalculateScore(context));
    }

    [Fact]
    public void CalculateScore_IgnoresRelativeStrength_WhenDisabled()
    {
        var engine = new RuleBasedScoringEngine(new StrategyConfiguration
        {
            UseRelativeStrength = false,
            RelativeStrengthScoreWeight = 3m
        });

        var score = engine.CalculateScore(
            Context(2m, 20m, 1m, true, relativeStrengthPercent: 10m), out var contribution);

        Assert.Equal(25m, score);
        Assert.Equal(0m, contribution);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 4)]
    public void CalculateScore_DoesNotApplyRelativeStrength_ToDisqualifiedStocks(
        bool aboveSma, bool qualityFilter, int volatility)
    {
        var engine = new RuleBasedScoringEngine(new StrategyConfiguration
        {
            UseRelativeStrength = true,
            UseQualityFilter = qualityFilter
        });

        var score = engine.CalculateScore(
            Context(2m, 20m, volatility, aboveSma, relativeStrengthPercent: 100m),
            out var contribution);

        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
        Assert.Equal(0m, contribution);
    }

    [Fact]
    public void CalculateScore_CombinesRelativeStrengthWithExistingFactors()
    {
        var engine = new RuleBasedScoringEngine(new StrategyConfiguration
        {
            UseRsi = true,
            Use52WeekHigh = true,
            RelativeStrengthScoreWeight = 2m
        });

        var score = engine.CalculateScore(
            Context(2m, 20m, 1m, true, rsi: 60m,
                distanceFromWeek52HighPercent: 0m, relativeStrengthPercent: -3m),
            out var contribution);

        Assert.Equal(-6m, contribution);
        Assert.Equal(25m + 5m + 5m - 6m, score);
    }

    [Fact]
    public void CalculateScore_ThrowsOnRelativeStrengthContributionOverflow()
    {
        var engine = new RuleBasedScoringEngine(new StrategyConfiguration
        {
            RelativeStrengthScoreWeight = 2m
        });

        Assert.Throws<OverflowException>(() => engine.CalculateScore(
            Context(0m, 0m, 0m, true, relativeStrengthPercent: decimal.MaxValue)));
    }

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

    [Fact]
    public void CalculateScore_LeavesLegacyScoreUnchanged_WhenRsiDisabled()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = false,
            RsiPreferredRangeBonus = 11m,
            RsiOverboughtPenalty = 7m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 2m, volumeTrend: 20m, volatility: 1m, isAboveSma5: true, rsi: 60m));

        Assert.Equal(2m * 10m + 20m * 0.05m - 1m + 5m, score);
    }

    [Fact]
    public void CalculateScore_AddsConfiguredBonus_WhenRsiInPreferredRange()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredMinimum = 50m,
            RsiPreferredMaximum = 70m,
            RsiOverboughtThreshold = 80m,
            RsiPreferredRangeBonus = 2.5m,
            RsiOverboughtPenalty = 9m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 1m, volumeTrend: 0m, volatility: 1m, isAboveSma5: true, rsi: 65m));

        Assert.Equal(1m * 10m - 1m + 5m + 2.5m, score);
    }

    [Fact]
    public void CalculateScore_AppliesConfiguredPenalty_WhenRsiAtOrAboveOverboughtThreshold()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredMinimum = 50m,
            RsiPreferredMaximum = 70m,
            RsiOverboughtThreshold = 70m,
            RsiPreferredRangeBonus = 2.5m,
            RsiOverboughtPenalty = 4m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 1m, volumeTrend: 0m, volatility: 1m, isAboveSma5: true, rsi: 70m));

        Assert.Equal(1m * 10m - 1m + 5m - 4m, score);
    }

    [Fact]
    public void CalculateScore_DoesNotAdjustForRsi_WhenOutsidePreferredRangeAndBelowOverbought()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredMinimum = 50m,
            RsiPreferredMaximum = 70m,
            RsiOverboughtThreshold = 80m,
            RsiPreferredRangeBonus = 2m,
            RsiOverboughtPenalty = 3m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 1m, volumeTrend: 0m, volatility: 1m, isAboveSma5: true, rsi: 49.99m));

        Assert.Equal(1m * 10m - 1m + 5m, score);
    }

    [Fact]
    public void CalculateScore_LeavesScoreUnchanged_WhenRsiIsMissing()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredRangeBonus = 2m,
            RsiOverboughtPenalty = 3m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 1m, volumeTrend: 0m, volatility: 1m, isAboveSma5: true));

        Assert.Equal(1m * 10m - 1m + 5m, score);
    }

    [Fact]
    public void CalculateScore_AppliesPreferredRangeBonus_OnRangeBoundaries()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredMinimum = 50m,
            RsiPreferredMaximum = 70m,
            RsiOverboughtThreshold = 80m,
            RsiPreferredRangeBonus = 3m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var scoreAtMin = engine.CalculateScore(
            Context(momentum: 0m, volumeTrend: 0m, volatility: 0m, isAboveSma5: true, rsi: 50m));
        var scoreAtMax = engine.CalculateScore(
            Context(momentum: 0m, volumeTrend: 0m, volatility: 0m, isAboveSma5: true, rsi: 70m));

        Assert.Equal(8m, scoreAtMin);
        Assert.Equal(8m, scoreAtMax);
    }

    [Fact]
    public void CalculateScore_KeepsDisqualifiedScore_WhenBelowSma5EvenWithRsiEnabled()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredRangeBonus = 10m,
            RsiOverboughtPenalty = 10m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(momentum: 10m, volumeTrend: 50m, volatility: 1m, isAboveSma5: false, rsi: 60m));

        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
    }

    [Fact]
    public void CalculateScore_KeepsQualityFilterDisqualification_WhenRsiEnabled()
    {
        var strategy = new StrategyConfiguration
        {
            UseQualityFilter = true,
            UseRsi = true,
            RsiPreferredRangeBonus = 10m,
            RsiOverboughtPenalty = 10m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(
                momentum: 10m,
                volumeTrend: 50m,
                volatility: StrategyConfiguration.QualityVolatilityThresholdPercent + 0.1m,
                isAboveSma5: true,
                rsi: 60m));

        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
    }

    [Fact]
    public void CalculateScore_LeavesLegacyScoreUnchanged_WhenWeek52HighDisabled()
    {
        var strategy = new StrategyConfiguration
        {
            Use52WeekHigh = false,
            Week52HighScoreWeight = 10m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(
                momentum: 2m,
                volumeTrend: 20m,
                volatility: 1m,
                isAboveSma5: true,
                distanceFromWeek52HighPercent: 0m));

        Assert.Equal(2m * 10m + 20m * 0.05m - 1m + 5m, score);
    }

    [Fact]
    public void CalculateScore_AddsConfiguredContribution_WhenAtWeek52High()
    {
        var strategy = new StrategyConfiguration
        {
            Use52WeekHigh = true,
            Week52HighScoreWeight = 7m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(
                momentum: 1m,
                volumeTrend: 0m,
                volatility: 1m,
                isAboveSma5: true,
                distanceFromWeek52HighPercent: 0m));

        Assert.Equal(1m * 10m - 1m + 5m + 7m, score);
    }

    [Fact]
    public void CalculateScore_AppliesNegativeContribution_WhenFarBelowWeek52High()
    {
        var strategy = new StrategyConfiguration
        {
            Use52WeekHigh = true,
            Week52HighScoreWeight = 6m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(
                momentum: 1m,
                volumeTrend: 0m,
                volatility: 1m,
                isAboveSma5: true,
                distanceFromWeek52HighPercent: -75m));

        Assert.Equal(1m * 10m - 1m + 5m - 3m, score);
    }

    [Fact]
    public void CalculateScore_LeavesScoreUnchanged_WhenWeek52HighDistanceIsMissing()
    {
        var strategy = new StrategyConfiguration
        {
            Use52WeekHigh = true,
            Week52HighScoreWeight = 6m
        };
        var engine = new RuleBasedScoringEngine(strategy);

        var score = engine.CalculateScore(
            Context(
                momentum: 1m,
                volumeTrend: 0m,
                volatility: 1m,
                isAboveSma5: true));

        Assert.Equal(1m * 10m - 1m + 5m, score);
    }
}
