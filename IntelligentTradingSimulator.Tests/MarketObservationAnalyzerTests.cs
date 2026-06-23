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
}
