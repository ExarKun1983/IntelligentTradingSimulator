using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioRebalancingServiceTests
{
    [Fact]
    public void Execute_TagsSellsAsRebalance_AndLeavesBuysUntagged()
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position
                {
                    Symbol = "AAPL",
                    Quantity = 10m,
                    AveragePrice = 100m,
                    CurrentPrice = 100m
                }
            ]
        };
        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "AAPL", AllocationPercent = 50m },
            new() { Symbol = "MSFT", AllocationPercent = 50m }
        };
        var prices = new Dictionary<string, decimal>
        {
            ["AAPL"] = 100m,
            ["MSFT"] = 100m
        };

        new PortfolioRebalancingService().Execute(
            portfolio,
            recommendations,
            prices,
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine());

        var sell = Assert.Single(portfolio.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(ExitReason.Rebalance, sell.ExitReason);
        Assert.Equal("AAPL", sell.Symbol);
        Assert.Equal(5m, sell.Quantity);
        var buy = Assert.Single(portfolio.Trades, x => x.Action == TradeActionType.Buy);
        Assert.Null(buy.ExitReason);
        Assert.Equal("MSFT", buy.Symbol);
        Assert.Equal(5m, buy.Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }
}
