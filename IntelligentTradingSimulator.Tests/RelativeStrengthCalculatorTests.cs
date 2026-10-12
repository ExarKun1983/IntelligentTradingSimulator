using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class RelativeStrengthCalculatorTests
{
    private static readonly DateTime Start = new(2024, 1, 5);
    private readonly RelativeStrengthCalculator _calculator = new();

    private static MarketCandle Candle(int day, decimal close) =>
        new() { Date = Start.AddDays(day), Close = close };

    [Theory]
    [InlineData(120, 110, 10)]
    [InlineData(110, 120, -10)]
    [InlineData(90, 80, 10)]
    [InlineData(100, 100, 0)]
    [InlineData(110, 110, 0)]
    public void Calculate_ReturnsDifferenceInPercentagePoints(
        decimal stockEnd, decimal benchmarkEnd, decimal expected)
    {
        var result = _calculator.Calculate(
            [Candle(0, 100), Candle(3, stockEnd)],
            [Candle(0, 100), Candle(3, benchmarkEnd)],
            Start, Start.AddDays(3));

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Calculate_UsesStockEffectiveEndpoints_NotBenchmarkOuterDates()
    {
        var result = _calculator.Calculate(
            [Candle(3, 120), Candle(1, 100)],
            [Candle(4, 1_000), Candle(3, 220), Candle(0, 10), Candle(1, 200)],
            Start, Start.AddDays(4));

        Assert.Equal(10m, result);
    }

    [Fact]
    public void Calculate_ExcludesHistoryBeforeLookbackAndAfterAnalysisDate()
    {
        var result = _calculator.Calculate(
            [Candle(-1, 1), Candle(0, 100), Candle(3, 120), Candle(4, 1_000)],
            [Candle(-1, 1_000), Candle(0, 200), Candle(3, 220), Candle(4, 1)],
            Start, Start.AddDays(3));

        Assert.Equal(10m, result);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(1, 2)]
    public void Calculate_ReturnsNull_WhenBenchmarkEndpointsDoNotMatch(int firstDay, int lastDay)
    {
        Assert.Null(_calculator.Calculate(
            [Candle(0, 100), Candle(3, 120)],
            [Candle(firstDay, 200), Candle(lastDay, 220)],
            Start, Start.AddDays(3)));
    }

    [Fact]
    public void Calculate_AllowsMissingInteriorSessions()
    {
        Assert.Equal(10m, _calculator.Calculate(
            [Candle(0, 100), Candle(1, 105), Candle(3, 120)],
            [Candle(0, 200), Candle(3, 220)],
            Start, Start.AddDays(3)));
    }

    [Fact]
    public void Calculate_ReturnsNull_ForMissingOrInsufficientData()
    {
        MarketCandle[] valid = [Candle(0, 100), Candle(3, 120)];

        Assert.Null(_calculator.Calculate(null, valid, Start, Start.AddDays(3)));
        Assert.Null(_calculator.Calculate(valid, null, Start, Start.AddDays(3)));
        Assert.Null(_calculator.Calculate([], valid, Start, Start.AddDays(3)));
        Assert.Null(_calculator.Calculate(valid, [], Start, Start.AddDays(3)));
        Assert.Null(_calculator.Calculate([Candle(0, 100)], valid, Start, Start.AddDays(3)));
        Assert.Null(_calculator.Calculate(valid, valid, Start.AddDays(4), Start.AddDays(3)));
        Assert.Null(_calculator.Calculate(
            [Candle(0, 100), Candle(0, 120)], valid, Start, Start.AddDays(3)));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    public void Calculate_ReturnsNull_ForDuplicateEndpoints(bool duplicateStock, int day)
    {
        var stock = new List<MarketCandle> { Candle(0, 100), Candle(3, 120) };
        var benchmark = new List<MarketCandle> { Candle(0, 200), Candle(3, 220) };
        (duplicateStock ? stock : benchmark).Add(Candle(day, 150));

        Assert.Null(_calculator.Calculate(stock, benchmark, Start, Start.AddDays(3)));
    }

    [Theory]
    [InlineData(0, 120, 200, 220)]
    [InlineData(-1, 120, 200, 220)]
    [InlineData(100, 0, 200, 220)]
    [InlineData(100, 120, 0, 220)]
    [InlineData(100, 120, 200, -1)]
    public void Calculate_ReturnsNull_ForNonPositiveEndpointPrices(
        decimal stockStart, decimal stockEnd, decimal benchmarkStart, decimal benchmarkEnd)
    {
        Assert.Null(_calculator.Calculate(
            [Candle(0, stockStart), Candle(3, stockEnd)],
            [Candle(0, benchmarkStart), Candle(3, benchmarkEnd)],
            Start, Start.AddDays(3)));
    }

    [Fact]
    public void Calculate_ReturnsNull_ForUnrepresentableReturn()
    {
        Assert.Null(_calculator.Calculate(
            [Candle(0, 1), Candle(3, decimal.MaxValue)],
            [Candle(0, 100), Candle(3, 110)],
            Start, Start.AddDays(3)));
    }
}
