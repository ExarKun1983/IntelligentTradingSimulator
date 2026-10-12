using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class AtrCalculatorTests
{
    [Fact]
    public void Calculate_AveragesInitialTrueRanges_ThenUsesWilderSmoothing()
    {
        var calculator = new AtrCalculator(3);
        MarketCandle[] candles =
        [
            Candle(10m, 10m, 10m),
            Candle(12m, 9m, 11m),
            Candle(16m, 14m, 15m),
            Candle(12m, 10m, 11m),
            Candle(14m, 10m, 12m),
            Candle(17m, 16m, 16m)
        ];

        // Initial ranges are 3, 5 (gap up), 5 (gap down); later ranges are 4 and 5.
        Assert.Equal(13m / 3m, calculator.Calculate(candles.Take(4).ToList()));
        var expected = (((13m / 3m) * 2m + 4m) / 3m * 2m + 5m) / 3m;
        Assert.Equal(expected, calculator.Calculate(candles));
        Assert.Equal(expected, calculator.Calculate(candles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(14)]
    public void Calculate_ReturnsNull_WhenHistoryIsInsufficient(int count)
    {
        var candles = Enumerable.Range(0, count)
            .Select(_ => Candle(11m, 9m, 10m)).ToList();

        Assert.Null(new AtrCalculator().Calculate(candles));
    }

    [Fact]
    public void Calculate_DefaultPeriodRequiresFifteenCandles()
    {
        var calculator = new AtrCalculator();
        var candles = Enumerable.Range(0, 15)
            .Select(_ => Candle(11m, 9m, 10m)).ToList();

        Assert.Equal(14, calculator.Period);
        Assert.Equal(2m, calculator.Calculate(candles));
    }

    [Fact]
    public void Calculate_ReturnsZero_ForFlatPrices()
    {
        var candles = Enumerable.Range(0, 15)
            .Select(_ => Candle(10m, 10m, 10m)).ToList();

        Assert.Equal(0m, new AtrCalculator().Calculate(candles));
    }

    [Fact]
    public void Calculate_PeriodOneReturnsLatestTrueRange()
    {
        MarketCandle[] candles =
        [
            Candle(10m, 10m, 10m),
            Candle(12m, 9m, 11m),
            Candle(16m, 14m, 15m)
        ];

        Assert.Equal(5m, new AtrCalculator(1).Calculate(candles));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositivePeriods(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AtrCalculator(period));
    }

    [Fact]
    public void Calculate_RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => new AtrCalculator().Calculate(null!));
    }

    [Theory]
    [InlineData(9, 10, 10)]
    [InlineData(10, 0, 5)]
    [InlineData(10, -1, 5)]
    [InlineData(11, 9, 0)]
    [InlineData(11, 9, 8)]
    [InlineData(11, 9, 12)]
    public void Calculate_ReturnsNull_ForInvalidPrices(decimal high, decimal low, decimal close)
    {
        var candles = Enumerable.Range(0, 15)
            .Select(_ => Candle(11m, 9m, 10m)).ToList();
        candles[0] = Candle(high, low, close);

        Assert.Null(new AtrCalculator().Calculate(candles));
    }

    [Fact]
    public void Calculate_ReturnsNull_OnArithmeticOverflow()
    {
        var candles = Enumerable.Range(0, 15)
            .Select(_ => Candle(decimal.MaxValue, 1m, 10m)).ToList();

        Assert.Null(new AtrCalculator().Calculate(candles));
    }

    private static MarketCandle Candle(decimal high, decimal low, decimal close) =>
        new() { High = high, Low = low, Close = close };
}
