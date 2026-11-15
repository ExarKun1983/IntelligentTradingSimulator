namespace IntelligentTradingSimulator.Tests.Orchestration.E2EFixtures;

public class WholeSharePositionSizerTests
{
    [Fact]
    public void CalculateReturnsExactQuantityForExactDivision()
    {
        Assert.Equal(4m, WholeSharePositionSizer.Calculate(100m, 25m));
    }

    [Theory]
    [InlineData(100, 30, 3)]
    [InlineData(20, 30, 0)]
    public void CalculateReturnsAffordableWholeQuantity(decimal budget, decimal unitPrice, decimal expected)
    {
        var quantity = WholeSharePositionSizer.Calculate(budget, unitPrice);

        Assert.Equal(expected, quantity);
        Assert.True(quantity * unitPrice <= budget);
    }

    [Fact]
    public void CalculateReturnsZeroForZeroBudget()
    {
        Assert.Equal(0m, WholeSharePositionSizer.Calculate(0m, 30m));
    }

    [Fact]
    public void CalculateThrowsForNegativeBudget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WholeSharePositionSizer.Calculate(-1m, 30m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CalculateThrowsForNonpositiveUnitPrice(decimal unitPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WholeSharePositionSizer.Calculate(100m, unitPrice));
    }
}
