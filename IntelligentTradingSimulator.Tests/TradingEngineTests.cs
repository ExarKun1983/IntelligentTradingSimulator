using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class TradingEngineTests
{
    private readonly TradingEngine _engine = new();

    [Theory]
    [InlineData(TradeActionType.Buy)]
    [InlineData(TradeActionType.Sell)]
    public void Execute_UsesProvidedSimulatedTimestamp(TradeActionType action)
    {
        var simulatedTimestamp = new DateTime(2024, 1, 15, 14, 30, 45, DateTimeKind.Utc);
        var portfolio = new Portfolio
        {
            Cash = 1_000m,
            Positions = [new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m }]
        };
        var decision = new TradingDecision { Symbol = "AAPL", Action = action, Amount = 500m };

        _engine.Execute(portfolio, decision, currentPrice: 100m, simulatedTimestamp: simulatedTimestamp);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(action, trade.Action);
        Assert.Equal(simulatedTimestamp, trade.Timestamp);
        Assert.Equal(DateTimeKind.Utc, trade.Timestamp.Kind);
    }

    [Theory]
    [InlineData(TradeActionType.Buy, false)]
    [InlineData(TradeActionType.Sell, false)]
    [InlineData(TradeActionType.Buy, true)]
    [InlineData(TradeActionType.Sell, true)]
    public void Execute_UsesUtcNowWithoutSimulatedTimestamp(TradeActionType action, bool explicitNull)
    {
        var portfolio = new Portfolio
        {
            Cash = 1_000m,
            Positions = [new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m }]
        };
        var decision = new TradingDecision { Symbol = "AAPL", Action = action, Amount = 500m };
        var before = DateTime.UtcNow;

        if (explicitNull)
        {
            _engine.Execute(portfolio, decision, currentPrice: 100m, simulatedTimestamp: null);
        }
        else
        {
            _engine.Execute(portfolio, decision, currentPrice: 100m);
        }

        var after = DateTime.UtcNow;
        var trade = Assert.Single(portfolio.Trades);
        Assert.InRange(trade.Timestamp, before, after);
        Assert.Equal(DateTimeKind.Utc, trade.Timestamp.Kind);
    }

    [Fact]
    public void Execute_Buy_ReducesCashAndCreatesPosition()
    {
        var portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m };
        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Buy,
            Amount = 1_000m
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        Assert.Equal(9_000m, portfolio.Cash);
        Assert.Single(portfolio.Positions);
        Assert.Equal("AAPL", portfolio.Positions[0].Symbol);
        Assert.Equal(10m, portfolio.Positions[0].Quantity);
    }

    [Fact]
    public void Execute_Buy_DoesNothing_WhenInsufficientCash()
    {
        var portfolio = new Portfolio { StartingCash = 500m, Cash = 500m };
        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Buy,
            Amount = 1_000m
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        Assert.Equal(500m, portfolio.Cash);
        Assert.Empty(portfolio.Positions);
    }

    [Fact]
    public void Execute_Sell_IncreasesCashAndReducesPosition()
    {
        var portfolio = new Portfolio
        {
            StartingCash = 0m,
            Cash = 0m,
            Positions =
            [
                new Position
                {
                    Symbol = "AAPL",
                    Quantity = 10m,
                    AveragePrice = 100m,
                    CurrentPrice = 120m
                }
            ]
        };

        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Sell,
            Amount = 600m
        };

        _engine.Execute(portfolio, decision, currentPrice: 120m);

        Assert.Equal(600m, portfolio.Cash);
        Assert.Equal(5m, portfolio.Positions[0].Quantity);
        Assert.Equal(ExitReason.Manual, Assert.Single(portfolio.Trades).ExitReason);
    }

    [Fact]
    public void Execute_Sell_RemovesPosition_WhenFullyLiquidated()
    {
        var portfolio = new Portfolio
        {
            Cash = 0m,
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 5m, CurrentPrice = 100m }
            ]
        };

        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Sell,
            Amount = 1_000m
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        Assert.Empty(portfolio.Positions);
        Assert.Equal(500m, portfolio.Cash);
    }

    [Fact]
    public void Execute_Buy_RecordsTrade()
    {
        var portfolio = new Portfolio { Cash = 10_000m };
        var decision = new TradingDecision
        {
            Symbol = "MSFT",
            Action = TradeActionType.Buy,
            Amount = 500m
        };

        _engine.Execute(portfolio, decision, currentPrice: 50m);

        Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Buy, portfolio.Trades[0].Action);
        Assert.Equal("MSFT", portfolio.Trades[0].Symbol);
        Assert.Null(portfolio.Trades[0].ExitReason);
    }

    [Theory]
    [InlineData(ExitReason.Manual)]
    [InlineData(ExitReason.Rebalance)]
    [InlineData(ExitReason.StopLoss)]
    [InlineData(ExitReason.TrailingStop)]
    public void Execute_Sell_RecordsExitReason(ExitReason reason)
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 5m, CurrentPrice = 100m }
            ]
        };
        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Sell,
            Amount = 1_000m,
            ExitReason = reason
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(reason, trade.ExitReason);
        Assert.Equal(5m, trade.Quantity);
        Assert.Equal(100m, trade.Price);
        Assert.Equal(500m, portfolio.Cash);
        Assert.Empty(portfolio.Positions);
    }

    [Fact]
    public void Execute_Buy_DoesNotRecordExitReason()
    {
        var portfolio = new Portfolio { Cash = 1_000m };
        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Buy,
            Amount = 500m,
            ExitReason = ExitReason.StopLoss
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        Assert.Null(Assert.Single(portfolio.Trades).ExitReason);
    }

    [Fact]
    public void Execute_Sell_WithNoPosition_DoesNotRecordTrade()
    {
        var portfolio = new Portfolio { Cash = 1_000m };
        var decision = new TradingDecision
        {
            Symbol = "AAPL",
            Action = TradeActionType.Sell,
            Amount = 500m,
            ExitReason = ExitReason.TrailingStop
        };

        _engine.Execute(portfolio, decision, currentPrice: 100m);

        Assert.Empty(portfolio.Trades);
        Assert.Equal(1_000m, portfolio.Cash);
    }
}
