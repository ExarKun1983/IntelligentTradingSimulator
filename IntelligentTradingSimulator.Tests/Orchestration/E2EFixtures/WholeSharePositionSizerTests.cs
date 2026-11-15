namespace IntelligentTradingSimulator.Tests.Orchestration.E2EFixtures;

public class WholeSharePositionSizerTests
{
    [Fact]
    public void CalculateReturnsExactQuantityForExactDivision()
    {
        Assert.Equal(4m, WholeSharePositionSizer.Calculate(100m, 25m));
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
