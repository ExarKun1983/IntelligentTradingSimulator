using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class Week52HighCalculatorTests
{
    private static MarketCandle Candle(
        decimal close,
        decimal? high = null) =>
        new()
        {
            Date = DateTime.Today,
            Open = close,
            Close = close,
            High = high ?? close,
            Low = close,
            Volume = 1_000_000m
        };

    [Fact]
    public void Calculate_SelectsHighestHigh_FromTrailing252Sessions()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 253)
            .Select(_ => Candle(100m))
            .ToList();
        candles[0].High = 500m;
        candles[125].High = 150m;

        var result = calculator.Calculate(candles);

        Assert.Equal(150m, result.Week52High);
    }

    [Fact]
    public void Calculate_ReturnsZeroDistance_WhenCurrentPriceEqualsHigh()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 252)
            .Select(_ => Candle(100m))
            .ToList();
        candles[^1] = Candle(120m);

        var result = calculator.Calculate(candles);

        Assert.Equal(120m, result.Week52High);
        Assert.Equal(0m, result.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public void Calculate_ReturnsNegativeDistance_WhenCurrentPriceIsBelowHigh()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 252)
            .Select(_ => Candle(100m))
            .ToList();
        candles[100].High = 120m;
        candles[^1] = Candle(108m);

        var result = calculator.Calculate(candles);

        Assert.Equal(120m, result.Week52High);
        Assert.Equal(-10m, result.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public void Calculate_ReturnsValues_WhenExactly252SessionsAreAvailable()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 252)
            .Select(index => Candle(100m + index))
            .ToList();

        var result = calculator.Calculate(candles);

        Assert.Equal(351m, result.Week52High);
        Assert.Equal(0m, result.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public void Calculate_ReturnsNullValues_WhenHistoryIsInsufficient()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 251)
            .Select(index => Candle(100m + index))
            .ToList();

        var result = calculator.Calculate(candles);

        Assert.Null(result.Week52High);
        Assert.Null(result.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public void Calculate_ReturnsNullValues_WhenHighIsNonPositive()
    {
        var calculator = new Week52HighCalculator();
        var candles = Enumerable.Range(0, 252)
            .Select(_ => Candle(-1m, high: 0m))
            .ToList();

        var result = calculator.Calculate(candles);

        Assert.Null(result.Week52High);
        Assert.Null(result.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public void Calculate_Throws_WhenCandlesAreNull()
    {
        var calculator = new Week52HighCalculator();

        Assert.Throws<ArgumentNullException>(() => calculator.Calculate(null!));
    }

    [Fact]
    public void Constructor_Throws_WhenTradingSessionsIsInvalid()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Week52HighCalculator(0));
    }
}
