namespace IntelligentTradingSimulator.Tests.Orchestration.E2EFixtures;

public class WholeShareBudgetCalculatorTests
{
    [Theory]
    [InlineData(120, 40, 3)]
    [InlineData(0, 40, 0)]
    public void CalculateReturnsExpectedQuantityForPassingInputs(
        decimal budget,
        decimal unitPrice,
        decimal expected)
    {
        Assert.Equal(expected, WholeShareBudgetCalculator.Calculate(budget, unitPrice));
    }

    [Theory]
    [InlineData(125, 40, 3)]
    [InlineData(15, 40, 0)]
    [InlineData(100, 40, 2)]
    [InlineData(20, 40, 0)]
    public void CalculateReturnsAffordableWholeShares(
        decimal budget,
        decimal unitPrice,
        decimal expected)
    {
        var quantity = WholeShareBudgetCalculator.Calculate(budget, unitPrice);

        Assert.Equal(expected, quantity);
        Assert.True(quantity * unitPrice <= budget);
    }

    [Fact]
    public void CalculateThrowsWhenBudgetIsNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WholeShareBudgetCalculator.Calculate(-1, 40));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-40)]
    public void CalculateThrowsWhenUnitPriceIsNotPositive(decimal unitPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WholeShareBudgetCalculator.Calculate(120, unitPrice));
    }
}
