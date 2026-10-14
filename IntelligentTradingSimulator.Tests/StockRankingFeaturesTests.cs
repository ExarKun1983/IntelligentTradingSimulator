using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class StockRankingFeaturesTests
{
    [Fact]
    public void Construction_LeavesOptionalFeaturesUnavailable_AndPreservesRequiredZeroValues()
    {
        var features = new StockRankingFeatures
        {
            Symbol = "TEST",
            AnalysisDate = new DateOnly(2024, 1, 15),
            MomentumPercent = 0m,
            AverageVolume = 0m,
            VolumeTrendPercent = 0m,
            VolatilityPercent = 0m,
            IsAboveSma5 = false
        };

        var context = features.ToScoringContext();

        Assert.Equal("TEST", context.Symbol);
        Assert.Equal(new DateOnly(2024, 1, 15), features.AnalysisDate);
        Assert.Equal(0m, context.MomentumPercent);
        Assert.Equal(0m, context.AverageVolume);
        Assert.Equal(0m, context.VolumeTrendPercent);
        Assert.Equal(0m, context.VolatilityPercent);
        Assert.False(context.IsAboveSma5);
        Assert.Null(context.Rsi);
        Assert.Null(features.Atr);
        Assert.Null(features.Week52High);
        Assert.Null(context.DistanceFromWeek52HighPercent);
        Assert.Null(features.RelativeStrengthBenchmarkSymbol);
        Assert.Null(context.RelativeStrengthPercent);
        Assert.Null(context.Fundamentals);
        Assert.Same(features, context.Features);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToScoringContext_MapsInputs_AndPreservesDeterministicScores(bool optionalAvailable)
    {
        var fundamentals = optionalAvailable
            ? new FundamentalData { Symbol = "TEST", RevenueGrowthPercent = 0m }
            : null;
        var features = new StockRankingFeatures
        {
            Symbol = "TEST",
            AnalysisDate = new DateOnly(2024, 1, 15),
            MomentumPercent = 5m,
            AverageVolume = 1_000_000m,
            VolumeTrendPercent = -2m,
            VolatilityPercent = 1m,
            IsAboveSma5 = true,
            Rsi = optionalAvailable ? 60m : null,
            Atr = optionalAvailable ? 2m : null,
            Week52High = optionalAvailable ? 120m : null,
            DistanceFromWeek52HighPercent = optionalAvailable ? -10m : null,
            RelativeStrengthBenchmarkSymbol = optionalAvailable ? "SPY" : null,
            RelativeStrengthPercent = optionalAvailable ? 0m : null,
            Fundamentals = fundamentals
        };
        var legacyContext = new ScoringContext
        {
            Symbol = "TEST",
            MomentumPercent = 5m,
            AverageVolume = 1_000_000m,
            VolumeTrendPercent = -2m,
            VolatilityPercent = 1m,
            IsAboveSma5 = true,
            Rsi = features.Rsi,
            DistanceFromWeek52HighPercent = features.DistanceFromWeek52HighPercent,
            RelativeStrengthPercent = features.RelativeStrengthPercent,
            Fundamentals = fundamentals
        };

        var context = features.ToScoringContext();

        Assert.Equal(legacyContext.Symbol, context.Symbol);
        Assert.Equal(legacyContext.MomentumPercent, context.MomentumPercent);
        Assert.Equal(legacyContext.AverageVolume, context.AverageVolume);
        Assert.Equal(legacyContext.VolumeTrendPercent, context.VolumeTrendPercent);
        Assert.Equal(legacyContext.VolatilityPercent, context.VolatilityPercent);
        Assert.Equal(legacyContext.IsAboveSma5, context.IsAboveSma5);
        Assert.Equal(legacyContext.Rsi, context.Rsi);
        Assert.Equal(legacyContext.DistanceFromWeek52HighPercent, context.DistanceFromWeek52HighPercent);
        Assert.Equal(legacyContext.RelativeStrengthPercent, context.RelativeStrengthPercent);
        Assert.Same(fundamentals, context.Fundamentals);
        Assert.Same(features, context.Features);
        Assert.Null(legacyContext.Features);
        if (optionalAvailable)
        {
            Assert.Equal(0m, context.Fundamentals!.RevenueGrowthPercent);
            Assert.Null(context.Fundamentals.EpsGrowthPercent);
            Assert.Null(context.Fundamentals.Margins);
            Assert.Null(context.Fundamentals.Debt);
        }

        var configuration = new StrategyConfiguration
        {
            UseRsi = true,
            Use52WeekHigh = true,
            UseRelativeStrength = true
        };
        IScoringEngine[] engines =
        [
            new CompositeScoringEngine(configuration),
            new RuleBasedScoringEngine(configuration)
        ];
        foreach (var engine in engines)
        {
            Assert.Equal(engine.CalculateScore(legacyContext), engine.CalculateScore(context));
        }
    }

    [Theory]
    [InlineData(false, 5)]
    [InlineData(false, 15)]
    [InlineData(true, 15)]
    public void Analyze_SharesCompleteFeatureRow_WithScorerAndResult(bool explicitDate, int count)
    {
        var start = new DateTime(2024, 1, 1);
        var candles = Enumerable.Range(0, count).Select(index => new MarketCandle
        {
            Date = start.AddDays(index),
            Open = 100m + index,
            Close = 100m + index,
            High = 101m + index,
            Low = 99m + index,
            Volume = 1_000_000m + index * 1000m
        }).ToList();
        var engine = new FeatureScoringEngine();
        var analyzer = new MarketObservationAnalyzer(
            engine, week52HighCalculator: new Week52HighCalculator(15));
        var analysisDate = explicitDate ? start.AddDays(count + 1) : (DateTime?)null;
        var fundamentals = new FundamentalData { Symbol = "TEST", Debt = 0m };

        var result = analyzer.Analyze(
            "TEST", candles, candles, candles, "SPY", analysisDate, fundamentals);

        var features = Assert.IsType<StockRankingFeatures>(result.Features);
        Assert.Same(features, engine.Features);
        Assert.Equal(result.Symbol, features.Symbol);
        Assert.Equal(DateOnly.FromDateTime(analysisDate ?? candles[^1].Date), features.AnalysisDate);
        Assert.Equal(result.MomentumPercent, features.MomentumPercent);
        Assert.Equal(result.AverageVolume, features.AverageVolume);
        Assert.Equal(result.VolumeTrendPercent, features.VolumeTrendPercent);
        Assert.Equal(result.VolatilityPercent, features.VolatilityPercent);
        Assert.Equal(result.IsAboveSma5, features.IsAboveSma5);
        Assert.Equal(result.Rsi, features.Rsi);
        Assert.Equal(result.Atr, features.Atr);
        Assert.Equal(result.Week52High, features.Week52High);
        Assert.Equal(result.DistanceFromWeek52HighPercent, features.DistanceFromWeek52HighPercent);
        Assert.Equal("SPY", features.RelativeStrengthBenchmarkSymbol);
        Assert.Equal(0m, features.RelativeStrengthPercent);
        Assert.Same(fundamentals, features.Fundamentals);
        Assert.Equal(features.MomentumPercent, result.Score);
        if (count < 15)
        {
            Assert.Null(features.Rsi);
            Assert.Null(features.Atr);
            Assert.Null(features.Week52High);
            Assert.Null(features.DistanceFromWeek52HighPercent);
        }
        else
        {
            Assert.NotNull(features.Rsi);
            Assert.NotNull(features.Atr);
            Assert.NotNull(features.Week52High);
            Assert.NotNull(features.DistanceFromWeek52HighPercent);
        }
    }

    [Fact]
    public void Analyze_WithoutBenchmarkOrFundamentals_DoesNotInventFeatures()
    {
        var candles = new[]
        {
            new MarketCandle
            {
                Date = new DateTime(2024, 1, 1), Close = 100m,
                High = 101m, Low = 99m, Volume = 1_000_000m
            }
        };

        var result = new MarketObservationAnalyzer().Analyze("TEST", candles);

        Assert.NotNull(result.Features);
        Assert.Null(result.Features.RelativeStrengthBenchmarkSymbol);
        Assert.Null(result.Features.RelativeStrengthPercent);
        Assert.Null(result.Features.Fundamentals);
    }

    private sealed class FeatureScoringEngine : IScoringEngine
    {
        public StockRankingFeatures? Features { get; private set; }

        public decimal CalculateScore(ScoringContext context)
        {
            Features = context.Features;
            return Features!.MomentumPercent;
        }
    }
}
