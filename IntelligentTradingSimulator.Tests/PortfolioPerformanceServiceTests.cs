using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioPerformanceServiceTests
{
    private readonly PortfolioPerformanceService _service = new();

    [Fact]
    public void Calculate_ReturnsZeroProfitLoss_WhenNoPositionsAndCashUnchanged()
    {
        var portfolio = new Portfolio
        {
            StartingCash = 10_000m,
            Cash = 10_000m
        };

        var result = _service.Calculate(portfolio);

        Assert.Equal(10_000m, result.PortfolioValue);
        Assert.Equal(0m, result.ProfitLoss);
        Assert.Equal(0m, result.ReturnPercent);
    }

    [Fact]
    public void Calculate_ReturnsPositiveProfitLoss_WhenPositionHasGained()
    {
        var portfolio = new Portfolio
        {
            StartingCash = 1_000m,
            Cash = 0m,
            Positions =
            [
                new Position
                {
                    Symbol = "AAPL",
                    Quantity = 10m,
                    AveragePrice = 100m,
                    CurrentPrice = 150m
                }
            ]
        };

        var result = _service.Calculate(portfolio);

        Assert.Equal(1_500m, result.PortfolioValue);
        Assert.Equal(500m, result.ProfitLoss);
    }

    [Fact]
    public void Calculate_ReturnPercent_IsProportionalToProfitLoss()
    {
        var portfolio = new Portfolio
        {
            StartingCash = 10_000m,
            Cash = 11_000m
        };

        var result = _service.Calculate(portfolio);

        Assert.Equal(10m, result.ReturnPercent);
    }
}
