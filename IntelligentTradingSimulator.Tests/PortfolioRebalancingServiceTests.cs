using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioRebalancingServiceTests
{
    [Fact]
    public void Execute_Sell_RecordsRebalanceExitReason()
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m }
            ]
        };
        var recommendations = new[]
        {
            new StockRecommendation { Symbol = "AAPL", AllocationPercent = 50m }
        };

        new PortfolioRebalancingService().Execute(
            portfolio,
            recommendations,
            new Dictionary<string, decimal> { ["AAPL"] = 100m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine());

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Sell, trade.Action);
        Assert.Equal(ExitReason.Rebalance, trade.ExitReason);
        Assert.Equal(5m, trade.Quantity);
        Assert.Equal(500m, portfolio.Cash);
        Assert.Equal(5m, Assert.Single(portfolio.Positions).Quantity);
    }
}
