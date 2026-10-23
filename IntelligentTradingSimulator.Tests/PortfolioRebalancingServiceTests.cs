using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioRebalancingServiceTests
{
    [Theory]
    [InlineData(90, 10, true)]
    [InlineData(89, 10, true)]
    [InlineData(90.01, 10, false)]
    [InlineData(110, 10, false)]
    [InlineData(94, 5, true)]
    [InlineData(94, 10, false)]
    [InlineData(97.5, 2.5, true)]
    public void Execute_StopLossUsesConfiguredInclusiveThreshold(
        double price, double percent, bool shouldSell)
    {
        var portfolio = new Portfolio
        {
            Cash = 100m,
            Positions = [new Position
            {
                Symbol = "AAPL", Quantity = 3.1234m, AveragePrice = 100m, CurrentPrice = 120m
            }]
        };

        new PortfolioRebalancingService().Execute(
            portfolio, [], new Dictionary<string, decimal> { ["AAPL"] = (decimal)price },
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(), (decimal)percent);

        if (shouldSell)
        {
            Assert.Empty(portfolio.Positions);
            var trade = Assert.Single(portfolio.Trades);
            Assert.Equal(TradeActionType.Sell, trade.Action);
            Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
            Assert.Equal(3.1234m, trade.Quantity);
            Assert.Equal((decimal)price, trade.Price);
            Assert.Equal(100m + 3.1234m * (decimal)price, portfolio.Cash);
        }
        else
        {
            Assert.Single(portfolio.Positions);
            Assert.Empty(portfolio.Trades);
            Assert.Equal(100m, portfolio.Cash);
        }
    }

    [Theory]
    [InlineData(null, 100, 10)]
    [InlineData(0, 100, 10)]
    [InlineData(-1, 100, 10)]
    [InlineData(80, 0, 10)]
    [InlineData(80, -100, 10)]
    [InlineData(80, 100, 0)]
    [InlineData(80, 100, -1)]
    public void Execute_StopLossSkipsMissingOrInvalidPositionData(
        int? price, int entryPrice, int quantity)
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position
            {
                Symbol = "AAPL", Quantity = (decimal)quantity,
                AveragePrice = (decimal)entryPrice, CurrentPrice = 50m
            }]
        };
        var prices = new Dictionary<string, decimal>();
        if (price.HasValue)
        {
            prices["AAPL"] = (decimal)price.Value;
        }

        new PortfolioRebalancingService().Execute(
            portfolio, [], prices, new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(), 10m);

        Assert.Empty(portfolio.Trades);
        Assert.Single(portfolio.Positions);
    }

    [Fact]
    public void Execute_StopLossIsDisabledByDefault()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position
            {
                Symbol = "AAPL", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 80m
            }]
        };

        new PortfolioRebalancingService().Execute(
            portfolio, [], new Dictionary<string, decimal> { ["AAPL"] = 80m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine());

        Assert.Empty(portfolio.Trades);
        Assert.Single(portfolio.Positions);
    }

    [Fact]
    public void Execute_StopLossLiquidatesPositionWhenRequestedNotionalRoundsToZero()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position
            {
                Symbol = "AAPL", Quantity = 0.00000001m, AveragePrice = 1m, CurrentPrice = 1m
            }]
        };

        new PortfolioRebalancingService().Execute(
            portfolio, [], new Dictionary<string, decimal> { ["AAPL"] = 0.1m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(), 10m);

        Assert.Empty(portfolio.Positions);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        Assert.Equal(0.00000001m, trade.Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }

    [Fact]
    public void Execute_BuysAllPlannedPositionsWhenRoundingUsesSlightlyMoreThanBudgetedCash()
    {
        var portfolio = new Portfolio { Cash = 400m };
        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "A", AllocationPercent = 25m },
            new() { Symbol = "B", AllocationPercent = 25m },
            new() { Symbol = "C", AllocationPercent = 25m },
            new() { Symbol = "D", AllocationPercent = 25m }
        };
        var prices = new Dictionary<string, decimal>
        {
            ["A"] = 6m,
            ["B"] = 6m,
            ["C"] = 6m,
            ["D"] = 6m
        };

        new PortfolioRebalancingService().Execute(
            portfolio, recommendations, prices,
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine());

        Assert.Equal(4, portfolio.Positions.Count);
        Assert.Equal(4, portfolio.Trades.Count);
        Assert.Equal(16.66666665m, portfolio.Positions.Single(x => x.Symbol == "D").Quantity);
        Assert.Equal(0.00000004m, portfolio.Cash);
    }

    [Fact]
    public void Execute_BudgetsFivePlannedBuysAgainstActualRemainingCash()
    {
        var portfolio = new Portfolio { Cash = 500m };
        var recommendations = Enumerable.Range(0, 5)
            .Select(index => new StockRecommendation
            {
                Symbol = $"S{index}",
                AllocationPercent = 20m
            })
            .ToList();
        var prices = recommendations.ToDictionary(
            recommendation => recommendation.Symbol,
            _ => 6m);

        new PortfolioRebalancingService().Execute(
            portfolio, recommendations, prices,
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine());

        Assert.Equal(5, portfolio.Positions.Count);
        Assert.Equal(5, portfolio.Trades.Count);
        Assert.Equal(16.66666665m, portfolio.Positions.Single(x => x.Symbol == "S4").Quantity);
        Assert.Equal(0.00000002m, portfolio.Cash);
    }

    [Fact]
    public void Execute_BudgetsMixedPriceBuysAgainstActualRemainingCash()
    {
        var portfolio = new Portfolio { Cash = 200m };
        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "A", AllocationPercent = 50m },
            new() { Symbol = "B", AllocationPercent = 50m }
        };
        var prices = new Dictionary<string, decimal>
        {
            ["A"] = 6m,
            ["B"] = 1m
        };

        new PortfolioRebalancingService().Execute(
            portfolio, recommendations, prices,
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine());

        Assert.Equal(2, portfolio.Positions.Count);
        Assert.Equal(2, portfolio.Trades.Count);
        Assert.Equal(99.99999998m, portfolio.Positions.Single(x => x.Symbol == "B").Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Execute_RejectsInvalidStopLossPercent(double percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioRebalancingService().Execute(
            new Portfolio(), [], new Dictionary<string, decimal>(),
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(), (decimal)percent));
    }

    [Fact]
    public void Execute_StopLossExitsMultiplePositionsBeforeRebalance_WithoutRebuyingThem()
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 90m },
                new Position { Symbol = "MSFT", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 80m }
            ]
        };
        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "AAPL", AllocationPercent = 50m },
            new() { Symbol = "MSFT", AllocationPercent = 25m },
            new() { Symbol = "GOOG", AllocationPercent = 25m }
        };

        new PortfolioRebalancingService().Execute(
            portfolio, recommendations,
            new Dictionary<string, decimal> { ["AAPL"] = 90m, ["MSFT"] = 80m, ["GOOG"] = 100m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine(), 10m);

        Assert.Equal(3, portfolio.Trades.Count);
        Assert.All(portfolio.Trades.Take(2), trade =>
        {
            Assert.Equal(TradeActionType.Sell, trade.Action);
            Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
        });
        Assert.Equal("GOOG", Assert.Single(portfolio.Positions).Symbol);
        Assert.Equal(1275m, portfolio.Cash);
        Assert.Equal(3, recommendations.Count);
    }

    [Fact]
    public void Execute_TagsSellsAsRebalance_AndLeavesBuysUntagged()
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m }
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

        Assert.Equal(2, portfolio.Trades.Count);
        var sell = Assert.Single(portfolio.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(ExitReason.Rebalance, sell.ExitReason);
        Assert.Equal(5m, sell.Quantity);
        var buy = Assert.Single(portfolio.Trades, x => x.Action == TradeActionType.Buy);
        Assert.Null(buy.ExitReason);
        Assert.Equal(5m, buy.Quantity);
        Assert.Equal(0m, portfolio.Cash);
        Assert.All(portfolio.Positions, x => Assert.Equal(500m, x.MarketValue));
    }

    [Fact]
    public void Execute_PreservesHeldPositionOutsideTopNWithoutAnExitSignal()
    {
        var holding = new Position
        {
            Symbol = "AAA", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 100m
        };
        var portfolio = new Portfolio
        {
            Cash = 0m,
            Positions = [holding]
        };
        var recommendations = new[]
        {
            new StockRecommendation { Symbol = "AAA", AllocationPercent = 0m, Score = 0.001m },
            new StockRecommendation { Symbol = "BBB", AllocationPercent = 100m, Score = 100m }
        };

        new PortfolioRebalancingService().Execute(
            portfolio,
            recommendations,
            new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 100m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(),
            maxPositions: 1);

        Assert.Same(holding, Assert.Single(portfolio.Positions));
        Assert.Equal(10m, holding.Quantity);
        Assert.Empty(portfolio.Trades);
        Assert.Equal(0m, portfolio.Cash);
    }

    [Fact]
    public void Execute_ReweightsSelectedHoldingAndNewCandidateTogether()
    {
        var portfolio = new Portfolio
        {
            Cash = 500m,
            Positions =
            [
                new Position { Symbol = "AAA", Quantity = 5m, AveragePrice = 100m, CurrentPrice = 100m }
            ]
        };
        var recommendations = new[]
        {
            new StockRecommendation { Symbol = "AAA", AllocationPercent = 50m, Score = 3m },
            new StockRecommendation { Symbol = "BBB", AllocationPercent = 33.33m, Score = 2m },
            new StockRecommendation { Symbol = "CCC", AllocationPercent = 16.67m, Score = 1m }
        };

        new PortfolioRebalancingService().Execute(
            portfolio,
            recommendations,
            new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 100m, ["CCC"] = 100m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(),
            maxPositions: 2);

        Assert.Equal(600m, portfolio.Positions.Single(x => x.Symbol == "AAA").MarketValue);
        Assert.Equal(400m, portfolio.Positions.Single(x => x.Symbol == "BBB").MarketValue);
        Assert.DoesNotContain(portfolio.Positions, x => x.Symbol == "CCC");
        Assert.Equal(0m, portfolio.Cash);
    }
}
