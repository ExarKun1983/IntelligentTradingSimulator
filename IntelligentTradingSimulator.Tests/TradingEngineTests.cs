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
    [InlineData(3, 33.33333333, 99.99999999)]
    [InlineData(6, 16.66666667, 100.00000002)]
    public void Execute_Buy_UsesExecutedNotionalForCashAndRecordsRoundedQuantity(
        decimal price, decimal expectedQuantity, decimal expectedNotional)
    {
        var portfolio = new Portfolio { Cash = 200m };
        var decision = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 100m };

        _engine.Execute(portfolio, decision, price);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(expectedQuantity, trade.Quantity);
        Assert.Equal(price, trade.Price);
        Assert.Equal(expectedQuantity, Assert.Single(portfolio.Positions).Quantity);
        Assert.Equal(expectedNotional, 200m - portfolio.Cash);
        Assert.Equal(Math.Round(trade.Quantity * trade.Price, TradingPrecisionPolicy.CashScale,
            MidpointRounding.AwayFromZero), 200m - portfolio.Cash);
    }

    [Fact]
    public void Execute_Buy_AtExactAffordabilityFloorsRoundedUpQuantity()
    {
        var portfolio = new Portfolio { Cash = 100m };
        var decision = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 100m };

        _engine.Execute(portfolio, decision, currentPrice: 6m);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(16.66666666m, trade.Quantity);
        Assert.Equal(0.00000004m, portfolio.Cash);
        Assert.True(portfolio.Cash >= 0m);
        Assert.True(trade.Quantity * trade.Price <= 100m);
    }

    [Theory]
    [InlineData(0, 100, 10)]
    [InlineData(-1, 100, 10)]
    [InlineData(1, 0, 10)]
    [InlineData(1, -1, 10)]
    [InlineData(0.000000001, 100, 10)]
    public void Execute_Buy_RejectsInvalidOrUnexecutableInputsWithoutMutation(
        decimal amount, decimal price, decimal cash)
    {
        var portfolio = new Portfolio { Cash = cash };
        var decision = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = amount };

        _engine.Execute(portfolio, decision, price);

        Assert.Equal(cash, portfolio.Cash);
        Assert.Empty(portfolio.Positions);
        Assert.Empty(portfolio.Trades);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(0.000000001, 100)]
    public void Execute_Sell_RejectsInvalidOrUnexecutableInputsWithoutMutation(decimal amount, decimal price)
    {
        var position = new Position { Symbol = "TEST", Quantity = 10m, AveragePrice = 10m };
        var portfolio = new Portfolio { Cash = 5m, Positions = [position] };
        var decision = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = amount };

        _engine.Execute(portfolio, decision, price);

        Assert.Equal(5m, portfolio.Cash);
        Assert.Same(position, Assert.Single(portfolio.Positions));
        Assert.Equal(10m, position.Quantity);
        Assert.Empty(portfolio.Trades);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void Execute_Buy_PreservesSupportedLargeValues(decimal price)
    {
        const decimal cash = 99_999_999_999_999_999_999.99999999m;
        var portfolio = new Portfolio { Cash = cash };
        var decision = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = cash };

        _engine.Execute(portfolio, decision, currentPrice: price);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(price == 1m ? cash : 16_666_666_666_666_666_666.66666666m, trade.Quantity);
        Assert.True(portfolio.Cash >= 0m);
        Assert.Equal(cash, portfolio.Cash + Math.Round(
            trade.Quantity * trade.Price,
            TradingPrecisionPolicy.CashScale,
            MidpointRounding.AwayFromZero));
    }

    [Fact]
    public void Execute_BuyAndSell_ReconcileCashAndHoldingsAcrossRepeatedRoundTrips()
    {
        var portfolio = new Portfolio { StartingCash = 1_000m, Cash = 1_000m };
        var timestamp = new DateTime(2025, 1, 1);

        for (var i = 0; i < 100; i++)
        {
            _engine.Execute(portfolio,
                new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 10m },
                3m, timestamp);
        }

        var heldQuantity = Assert.Single(portfolio.Positions).Quantity;
        Assert.Equal(333.333333m, heldQuantity);
        Assert.Equal(0.000001m, portfolio.Cash);

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 1_000m },
            3m, timestamp);

        Assert.Empty(portfolio.Positions);
        Assert.Equal(1_000m, portfolio.Cash);
        Assert.Equal(portfolio.StartingCash + portfolio.Trades.Sum(trade =>
            (trade.Action == TradeActionType.Buy ? -1m : 1m)
            * Math.Round(trade.Quantity * trade.Price, TradingPrecisionPolicy.CashScale,
                MidpointRounding.AwayFromZero)), portfolio.Cash);
    }

    [Fact]
    public void Execute_Sell_FloorsPartialQuantityAndLiquidatesLegacyQuantityExactly()
    {
        var portfolio = new Portfolio
        {
            Cash = 0m,
            Positions = [new Position { Symbol = "TEST", Quantity = 1.23m, AveragePrice = 3m }]
        };

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 1m },
            currentPrice: 3m);

        Assert.Equal(0.89666667m, Assert.Single(portfolio.Positions).Quantity);
        Assert.Equal(0.99999999m, portfolio.Cash);
        Assert.Equal(0.33333333m, Assert.Single(portfolio.Trades).Quantity);

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 100m },
            currentPrice: 3m);

        Assert.Empty(portfolio.Positions);
        Assert.Equal(1.23m, portfolio.Trades.Sum(trade =>
            trade.Action == TradeActionType.Sell ? trade.Quantity : 0m));
        Assert.Equal(3.69m, portfolio.Cash);
    }

    [Fact]
    public void Execute_Sell_DerivesPartialQuantityFromOriginalAmount()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position { Symbol = "TEST", Quantity = 20_000m, AveragePrice = 0.000000000001m }]
        };

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 0.000000005m },
            currentPrice: 0.000000000001m);

        var remainingPosition = Assert.Single(portfolio.Positions);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(5_000m, trade.Quantity);
        Assert.Equal(15_000m, remainingPosition.Quantity);
        Assert.Equal(0.00000001m, portfolio.Cash);
    }

    [Fact]
    public void Execute_Sell_DoesNotLiquidateWhenOnlyRoundedRequestMatchesFullNotional()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position { Symbol = "TEST", Quantity = 1m, AveragePrice = 1.000000004m }]
        };

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 1.000000001m },
            currentPrice: 1.000000004m);

        var remainingPosition = Assert.Single(portfolio.Positions);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(0.99999999m, trade.Quantity);
        Assert.Equal(0.00000001m, remainingPosition.Quantity);
    }

    [Fact]
    public void Execute_Sell_UsesRoundedFullNotionalToAvoidLiquidationDust()
    {
        const decimal quantity = 3.33333333m;
        const decimal price = 59.853456789123m;
        var portfolio = new Portfolio
        {
            Positions = [new Position { Symbol = "TEST", Quantity = quantity, AveragePrice = price }]
        };
        var amount = Math.Round(quantity * price, TradingPrecisionPolicy.CashScale,
            MidpointRounding.AwayFromZero);

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = amount },
            price);

        Assert.Empty(portfolio.Positions);
        Assert.Equal(quantity, Assert.Single(portfolio.Trades).Quantity);
        Assert.Equal(amount, portfolio.Cash);
    }

    [Fact]
    public void Execute_Sell_FullLiquidationClosesSupportedQuantityWhenCashNotionalRoundsToZero()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position { Symbol = "TEST", Quantity = 0.00000001m, AveragePrice = 0.000000000001m }]
        };

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Sell, Amount = 0.00000001m },
            currentPrice: 0.000000000001m);

        Assert.Empty(portfolio.Positions);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(0.00000001m, trade.Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }

    [Fact]
    public void Execute_Sell_OversizedRequestFullyLiquidatesBeforeQuantityDivision()
    {
        var simulatedTimestamp = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var portfolio = new Portfolio
        {
            Cash = 0m,
            Positions = [new Position { Symbol = "TEST", Quantity = 10_000m, AveragePrice = 0.000000000001m }]
        };

        _engine.Execute(portfolio,
            new TradingDecision
            {
                Symbol = "TEST",
                Action = TradeActionType.Sell,
                Amount = 100_000_000_000_000_000m,
                ExitReason = ExitReason.StopLoss
            },
            currentPrice: 0.000000000001m,
            simulatedTimestamp);

        Assert.Empty(portfolio.Positions);
        Assert.Equal(0.00000001m, portfolio.Cash);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Sell, trade.Action);
        Assert.Equal(10_000m, trade.Quantity);
        Assert.Equal(0.000000000001m, trade.Price);
        Assert.Equal(simulatedTimestamp, trade.Timestamp);
        Assert.Equal(DateTimeKind.Utc, trade.Timestamp.Kind);
        Assert.Equal(ExitReason.StopLoss, trade.ExitReason);
    }

    [Fact]
    public void Execute_RepeatedBuys_UsesBoundedWeightedAverageCostAndUnroundedPrice()
    {
        const decimal firstPrice = 3m;
        const decimal secondPrice = 7m;
        var portfolio = new Portfolio { Cash = 100m };

        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 10m }, firstPrice);
        _engine.Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 20m }, secondPrice);

        var position = Assert.Single(portfolio.Positions);
        Assert.Equal(6.19047619m, position.Quantity);
        Assert.Equal(4.846153848142m, position.AveragePrice);
        Assert.Equal(12, GetScale(position.AveragePrice));
        Assert.Equal(secondPrice, position.CurrentPrice);
        Assert.Equal(TradingPrecisionPolicy.QuantityScale, GetScale(position.Quantity));
        Assert.Collection(portfolio.Trades,
            firstTrade =>
            {
                Assert.Equal(3.33333333m, firstTrade.Quantity);
                Assert.Equal(firstPrice, firstTrade.Price);
            },
            secondTrade =>
            {
                Assert.Equal(2.85714286m, secondTrade.Quantity);
                Assert.Equal(secondPrice, secondTrade.Price);
            });
    }

    private static int GetScale(decimal value) =>
        (decimal.GetBits(value)[3] >> 16) & 0x7F;

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
