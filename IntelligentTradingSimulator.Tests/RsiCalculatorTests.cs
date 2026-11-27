using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class RsiCalculatorTests
{
    private readonly RsiCalculator _calculator = new();

    [Fact]
    public void Calculate_ReturnsWilderRsi_ForMixedPriceChanges()
    {
        decimal[] closingPrices =
        [
            44.34m, 44.09m, 44.15m, 43.61m, 44.33m,
            44.83m, 45.10m, 45.42m, 45.84m, 46.08m,
            45.89m, 46.03m, 45.61m, 46.28m, 46.28m,
            46.00m, 46.50m, 46.20m, 46.90m, 46.70m
        ];

        var rsi = _calculator.Calculate(closingPrices);

        Assert.NotNull(rsi);
        Assert.Equal(67.26415757m, rsi.Value, 6);
    }

    [Theory]
    [InlineData(true, 100)]
    [InlineData(false, 0)]
    public void Calculate_ReturnsBoundaryValue_ForOnlyGainsOrLosses(
        bool gains,
        decimal expected)
    {
        var closingPrices = Enumerable.Range(0, 15)
            .Select(index => gains ? 100m + index : 100m - index)
            .ToList();

        var rsi = _calculator.Calculate(closingPrices);

        Assert.Equal(expected, rsi);
    }

    [Fact]
    public void Calculate_ReturnsNull_WhenFewerThanFifteenPricesAreAvailable()
    {
        var closingPrices = Enumerable.Range(0, 14)
            .Select(index => 100m + index)
            .ToList();

        var rsi = _calculator.Calculate(closingPrices);

        Assert.Null(rsi);
    }

    [Fact]
    public void Calculate_ReturnsNeutralValue_ForFlatPrices()
    {
        var closingPrices = Enumerable.Repeat(42m, 15).ToList();

        var rsi = _calculator.Calculate(closingPrices);

        Assert.Equal(50m, rsi);
    }

    [Fact]
    public void Calculate_AppliesWilderSmoothing_AfterCustomPeriodInitialization()
    {
        var calculator = new RsiCalculator(period: 3);
        decimal[] closingPrices = [10m, 11m, 10m, 12m, 11m];

        var rsi = calculator.Calculate(closingPrices);

        Assert.NotNull(rsi);
        Assert.Equal(54.545454545m, rsi.Value, 9);
    }

    [Fact]
    public void Calculate_UsesCustomPeriodForMinimumPriceCount()
    {
        var calculator = new RsiCalculator(period: 3);

        var rsiWithPeriodPrices = calculator.Calculate([10m, 11m, 10m]);
        var rsiWithPeriodPlusOnePrices = calculator.Calculate([10m, 11m, 10m, 12m]);

        Assert.Null(rsiWithPeriodPrices);
        Assert.NotNull(rsiWithPeriodPlusOnePrices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenPeriodIsNotPositive(int period)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RsiCalculator(period));
    }

    [Fact]
    public void Calculate_Throws_WhenClosingPricesAreNull()
    {
        Assert.Throws<ArgumentNullException>(() => _calculator.Calculate(null!));
    }
}
