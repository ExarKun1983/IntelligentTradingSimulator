using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class MarketObservationAnalyzerTests
{
    private readonly MarketObservationAnalyzer _analyzer = new();

    private static MarketCandle Candle(
        decimal close,
        decimal volume = 1_000_000,
        decimal? high = null,
        decimal? low = null) =>
        new()
        {
            Date = DateTime.Today,
            Open = close,
            Close = close,
            High = high ?? close * 1.01m,
            Low = low ?? close * 0.99m,
            Volume = volume
        };

    [Fact]
    public void Analyze_AboveSma5_WhenLastCloseExceedsFiveDayAverage()
    {
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(100m), Candle(100m),
            Candle(100m), Candle(100m), Candle(110m)
        };

        var result = _analyzer.Analyze("TEST", candles);

        Assert.True(result.IsAboveSma5);
    }

    [Fact]
    public void Analyze_NotAboveSma5_WhenLastCloseBelowFiveDayAverage()
    {
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(100m), Candle(100m),
            Candle(100m), Candle(100m), Candle(90m)
        };

        var result = _analyzer.Analyze("TEST", candles);

        Assert.False(result.IsAboveSma5);
    }

    [Fact]
    public void Analyze_ScoreIsNegative_WhenNotAboveSma5()
    {
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(100m), Candle(100m),
            Candle(100m), Candle(100m), Candle(90m)
        };

        var result = _analyzer.Analyze("TEST", candles);

        Assert.True(result.Score < 0);
    }

    [Fact]
    public void Analyze_MomentumIsPositive_WhenPriceRises()
    {
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(101m), Candle(102m),
            Candle(103m), Candle(104m), Candle(110m)
        };

        var result = _analyzer.Analyze("TEST", candles);

        Assert.True(result.MomentumPercent > 0);
    }

    [Fact]
    public void Analyze_ReturnsCorrectSymbol()
    {
        var candles = Enumerable.Range(0, 6)
            .Select(_ => Candle(100m))
            .ToList();

        var result = _analyzer.Analyze("AAPL", candles);

        Assert.Equal("AAPL", result.Symbol);
    }

    [Fact]
    public void Analyze_HighVolatility_GetsNegativeScore_WhenQualityFilterEnabled()
    {
        var configWithFilter = new StrategyConfiguration { UseQualityFilter = true };
        var analyzer = new MarketObservationAnalyzer(configWithFilter);

        // candles with 5% intraday range (volatility > 3% threshold)
        var candles = new List<MarketCandle>
        {
            Candle(100m, high: 102.5m, low: 97.5m),
            Candle(101m, high: 103.5m, low: 98.5m),
            Candle(103m, high: 105.5m, low: 100.5m),
            Candle(105m, high: 107.5m, low: 102.5m),
            Candle(107m, high: 109.5m, low: 104.5m),
            Candle(110m, high: 112.5m, low: 107.5m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.True(result.Score < 0);
    }

    [Fact]
    public void Analyze_HighVolatility_CanGetPositiveScore_WhenQualityFilterDisabled()
    {
        var configWithoutFilter = new StrategyConfiguration { UseQualityFilter = false };
        var analyzer = new MarketObservationAnalyzer(configWithoutFilter);

        // same candles: rising price (good momentum), 5% intraday range
        var candles = new List<MarketCandle>
        {
            Candle(100m, high: 102.5m, low: 97.5m),
            Candle(101m, high: 103.5m, low: 98.5m),
            Candle(103m, high: 105.5m, low: 100.5m),
            Candle(105m, high: 107.5m, low: 102.5m),
            Candle(107m, high: 109.5m, low: 104.5m),
            Candle(110m, high: 112.5m, low: 107.5m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Analyze_QualityFilter_DoesNotPenalizeLowVolatilityStock()
    {
        var configWithFilter = new StrategyConfiguration { UseQualityFilter = true };
        var analyzer = new MarketObservationAnalyzer(configWithFilter);

        // default candles have ~2% range — below the 3% threshold
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(101m), Candle(102m),
            Candle(103m), Candle(104m), Candle(110m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.True(result.Score > 0);
    }

    [Fact]
    public void Analyze_DelegatesScoringToScoringEngine()
    {
        var engine = new CapturingScoringEngine(42m);
        var analyzer = new MarketObservationAnalyzer(engine);

        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(101m), Candle(102m),
            Candle(103m), Candle(104m), Candle(110m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.Equal(42m, result.Score);
        Assert.NotNull(engine.LastContext);
        Assert.Equal("TEST", engine.LastContext!.Symbol);
        Assert.Equal(result.MomentumPercent, engine.LastContext.MomentumPercent);
        Assert.Equal(result.AverageVolume, engine.LastContext.AverageVolume);
        Assert.Equal(result.VolumeTrendPercent, engine.LastContext.VolumeTrendPercent);
        Assert.Equal(result.VolatilityPercent, engine.LastContext.VolatilityPercent);
        Assert.Equal(result.IsAboveSma5, engine.LastContext.IsAboveSma5);
        Assert.Equal(result.Rsi, engine.LastContext.Rsi);
    }

    [Fact]
    public void Analyze_ScoreMatchesRuleBasedScoringEngine()
    {
        var candles = new List<MarketCandle>
        {
            Candle(100m), Candle(101m), Candle(102m),
            Candle(103m), Candle(104m), Candle(110m)
        };

        var result = _analyzer.Analyze("TEST", candles);

        var expected = new RuleBasedScoringEngine().CalculateScore(
            new ScoringContext
            {
                Symbol = "TEST",
                MomentumPercent = result.MomentumPercent,
                AverageVolume = result.AverageVolume,
                VolumeTrendPercent = result.VolumeTrendPercent,
                VolatilityPercent = result.VolatilityPercent,
                IsAboveSma5 = result.IsAboveSma5
            });

        Assert.Equal(expected, result.Score);
    }

    [Fact]
    public void Analyze_ExposesRsi_WithoutChangingScore()
    {
        var engine = new CapturingScoringEngine(42m);
        var analyzer = new MarketObservationAnalyzer(engine);
        var candles = Enumerable.Range(0, 15)
            .Select(index => Candle(100m + index))
            .ToList();

        var result = analyzer.Analyze("TEST", candles);

        Assert.Equal(100m, result.Rsi);
        Assert.Equal(42m, result.Score);
    }

    [Fact]
    public void Analyze_ExposesWeek52High_WithoutChangingScore()
    {
        var engine = new CapturingScoringEngine(42m);
        var analyzer = new MarketObservationAnalyzer(
            engine,
            week52HighCalculator: new Week52HighCalculator(3));
        var candles = new List<MarketCandle>
        {
            Candle(100m, high: 100m),
            Candle(120m, high: 120m),
            Candle(108m, high: 108m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.Equal(120m, result.Week52High);
        Assert.Equal(-10m, result.DistanceFromWeek52HighPercent);
        Assert.Equal(42m, result.Score);
        Assert.NotNull(engine.LastContext);
        Assert.Equal(result.Rsi, engine.LastContext!.Rsi);
    }

    [Fact]
    public void Analyze_Week52HighIsNull_WhenHistoryIsInsufficient()
    {
        var analyzer = new MarketObservationAnalyzer(
            new CapturingScoringEngine(42m),
            week52HighCalculator: new Week52HighCalculator(3));
        var candles = new List<MarketCandle>
        {
            Candle(100m),
            Candle(101m)
        };

        var result = analyzer.Analyze("TEST", candles);

        Assert.Null(result.Week52High);
        Assert.Null(result.DistanceFromWeek52HighPercent);
        Assert.Equal(42m, result.Score);
    }

    [Fact]
    public void Analyze_RsiIsNull_WhenExistingFiveCandleHistoryIsInsufficient()
    {
        var candles = Enumerable.Range(0, 5)
            .Select(index => Candle(100m + index))
            .ToList();

        var result = _analyzer.Analyze("TEST", candles);

        Assert.Null(result.Rsi);
    }

    [Fact]
    public void Analyze_UsesRsiAdjustedScore_WhenStrategyEnablesRsi()
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = true,
            RsiPreferredMinimum = 50m,
            RsiPreferredMaximum = 70m,
            RsiOverboughtThreshold = 80m,
            RsiPreferredRangeBonus = 2m,
            RsiOverboughtPenalty = 4m
        };
        var analyzer = new MarketObservationAnalyzer(strategy);
        var candles = Enumerable.Range(0, 15)
            .Select(index => Candle(100m + index))
            .ToList();

        var result = analyzer.Analyze("TEST", candles);

        var expected = new RuleBasedScoringEngine(strategy).CalculateScore(
            new ScoringContext
            {
                Symbol = "TEST",
                MomentumPercent = result.MomentumPercent,
                AverageVolume = result.AverageVolume,
                VolumeTrendPercent = result.VolumeTrendPercent,
                VolatilityPercent = result.VolatilityPercent,
                IsAboveSma5 = result.IsAboveSma5,
                Rsi = result.Rsi
            });

        Assert.Equal(expected, result.Score);
    }

    private sealed class CapturingScoringEngine : IScoringEngine
    {
        private readonly decimal _score;

        public CapturingScoringEngine(decimal score) => _score = score;

        public ScoringContext? LastContext { get; private set; }

        public decimal CalculateScore(ScoringContext context)
        {
            LastContext = context;
            return _score;
        }
    }
}
