using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class TrailingStopTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("10")]
    [InlineData("2.5")]
    [InlineData("100")]
    public void LoadAndLoadAll_BindOptionalTrailingStopPercent(string? value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Strategies:default:MaxPositions"] = "10",
                ["Strategies:default:TrailingStopPercent"] = value
            }).Build();
        var expected = value is null ? (decimal?)null :
            decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(expected, StrategyConfigurationLoader.Load(configuration).TrailingStopPercent);
        Assert.Equal(expected, Assert.Single(StrategyConfigurationLoader.LoadAll(configuration)).TrailingStopPercent);
        Assert.Null(new StrategyConfiguration().TrailingStopPercent);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("100.01")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void LoadAndLoadAll_RejectInvalidTrailingStopPercent(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Strategies:default:TrailingStopPercent"] = value }).Build();

        Assert.Contains("Strategies:default:TrailingStopPercent", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.Load(configuration)).Message);
        Assert.Contains("Strategies:default:TrailingStopPercent", Assert.Throws<InvalidOperationException>(() =>
            StrategyConfigurationLoader.LoadAll(configuration)).Message);
    }

    [Theory]
    [InlineData(108, 10, true)]
    [InlineData(107, 10, true)]
    [InlineData(108.01, 10, false)]
    [InlineData(130, 10, false)]
    [InlineData(114, 5, true)]
    [InlineData(114, 10, false)]
    [InlineData(117, 2.5, true)]
    [InlineData(1, 100, false)]
    public void Execute_TrailingStopUsesHighRatherThanEntry_AndInclusiveConfiguredThreshold(
        double price, double percent, bool shouldSell)
    {
        var position = new Position
        {
            Symbol = "AAPL", Quantity = 3.1234m, AveragePrice = 100m, CurrentPrice = 120m
        };
        var portfolio = new Portfolio { Cash = 100m, Positions = [position] };

        Execute(portfolio, new Dictionary<string, decimal> { ["AAPL"] = (decimal)price }, (decimal)percent);

        Assert.Equal(Math.Max(120m, (decimal)price), position.HighestPriceSinceEntry);
        if (shouldSell)
        {
            Assert.Empty(portfolio.Positions);
            var trade = Assert.Single(portfolio.Trades);
            Assert.Equal(TradeActionType.Sell, trade.Action);
            Assert.Equal(ExitReason.TrailingStop, trade.ExitReason);
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

    [Fact]
    public void Execute_TracksNewHighWhileDisabled_AndDoesNotLowerItOnDrawdown()
    {
        var position = new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m };
        var portfolio = new Portfolio { Positions = [position] };
        Execute(portfolio, new Dictionary<string, decimal> { ["AAPL"] = 150m });
        Execute(portfolio, new Dictionary<string, decimal> { ["AAPL"] = 140m }, 10m);

        Assert.Equal(150m, position.HighestPriceSinceEntry);
        Assert.Empty(portfolio.Trades);

        Execute(portfolio, new Dictionary<string, decimal> { ["AAPL"] = 135m }, 10m);
        Assert.Empty(portfolio.Positions);
        Assert.Equal(ExitReason.TrailingStop, Assert.Single(portfolio.Trades).ExitReason);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(100, 0)]
    [InlineData(100, -1)]
    public void Execute_SkipsMissingOrInvalidPriceAndQuantity(int? price, int quantity)
    {
        var position = new Position { Symbol = "AAPL", Quantity = quantity, CurrentPrice = 120m };
        var portfolio = new Portfolio { Positions = [position] };
        var prices = new Dictionary<string, decimal>();
        if (price.HasValue)
        {
            prices["AAPL"] = price.Value;
        }

        Execute(portfolio, prices, 10m);

        Assert.Single(portfolio.Positions);
        Assert.Empty(portfolio.Trades);
        Assert.Equal(120m, position.HighestPriceSinceEntry);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.01)]
    public void Execute_RejectsInvalidTrailingStopPercent(double percent)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Execute(new Portfolio(), new Dictionary<string, decimal>(), (decimal)percent));
        Assert.Equal("trailingStopPercent", exception.ParamName);
    }

    [Fact]
    public void Execute_TrailingStopIsDisabledByDefault()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 120m }]
        };
        Execute(portfolio, new Dictionary<string, decimal> { ["AAPL"] = 80m });
        Assert.Single(portfolio.Positions);
        Assert.Empty(portfolio.Trades);
    }

    [Fact]
    public void Execute_ExitsMultiplePositionsBeforeRebalance_WithoutRebuyingThem()
    {
        var portfolio = new Portfolio
        {
            Positions =
            [
                new Position { Symbol = "AAPL", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 150m },
                new Position { Symbol = "MSFT", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 120m }
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
            new Dictionary<string, decimal> { ["AAPL"] = 135m, ["MSFT"] = 108m, ["GOOG"] = 100m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine(),
            trailingStopPercent: 10m);

        Assert.Equal(3, portfolio.Trades.Count);
        Assert.All(portfolio.Trades.Take(2), trade =>
        {
            Assert.Equal(TradeActionType.Sell, trade.Action);
            Assert.Equal(ExitReason.TrailingStop, trade.ExitReason);
        });
        Assert.Equal("GOOG", Assert.Single(portfolio.Positions).Symbol);
        Assert.Equal(3, recommendations.Count);
    }

    [Fact]
    public void Execute_StopLossTakesPrecedenceWhenBothStopsAreBreached()
    {
        var portfolio = new Portfolio
        {
            Positions = [new Position
            {
                Symbol = "AAPL", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 120m
            }]
        };
        new PortfolioRebalancingService().Execute(
            portfolio, [], new Dictionary<string, decimal> { ["AAPL"] = 90m },
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine(), 10m, 10m);
        Assert.Equal(ExitReason.StopLoss, Assert.Single(portfolio.Trades).ExitReason);
    }

    [Fact]
    public void TradingEngine_InitializesHigh_PreservesItOnAddsAndPartialSells_AndResetsOnReentry()
    {
        var portfolio = new Portfolio { Cash = 10_000m };
        var engine = new TradingEngine();
        var buy = new TradingDecision { Symbol = "AAPL", Action = TradeActionType.Buy, Amount = 1000m };
        engine.Execute(portfolio, buy, 100m);
        var position = Assert.Single(portfolio.Positions);
        Assert.Equal(100m, position.HighestPriceSinceEntry);
        position.CurrentPrice = 150m;
        engine.Execute(portfolio, buy, 120m);
        Assert.Equal(150m, position.HighestPriceSinceEntry);
        engine.Execute(portfolio,
            new TradingDecision { Symbol = "AAPL", Action = TradeActionType.Sell, Amount = 120m }, 120m);
        Assert.Equal(150m, Assert.Single(portfolio.Positions).HighestPriceSinceEntry);
        engine.Execute(portfolio,
            new TradingDecision { Symbol = "AAPL", Action = TradeActionType.Sell, Amount = 10_000m }, 120m);
        engine.Execute(portfolio, buy, 80m);
        Assert.Equal(80m, Assert.Single(portfolio.Positions).HighestPriceSinceEntry);
    }

    [Fact]
    public void Migration_AddsHigh_InitializesExistingPositions_AndCanBeReversed()
    {
        var migration = new AddPositionHighestPriceSinceEntry();
        Assert.Equal(2, migration.UpOperations.Count);
        var added = Assert.IsType<AddColumnOperation>(migration.UpOperations[0]);
        Assert.Equal("Positions", added.Table);
        Assert.Equal("HighestPriceSinceEntry", added.Name);
        Assert.Equal(typeof(decimal), added.ClrType);
        Assert.False(added.IsNullable);
        Assert.Equal(0m, added.DefaultValue);
        var initialization = Assert.IsType<SqlOperation>(migration.UpOperations[1]);
        Assert.Contains("[AveragePrice]", initialization.Sql);
        Assert.Contains("[CurrentPrice]", initialization.Sql);
        var removed = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("Positions", removed.Table);
        Assert.Equal("HighestPriceSinceEntry", removed.Name);
    }

    private static void Execute(
        Portfolio portfolio, IReadOnlyDictionary<string, decimal> prices, decimal? percent = null) =>
        new PortfolioRebalancingService().Execute(
            portfolio, [], prices, new PortfolioAllocationEngine(new PortfolioAllocationService()),
            new TradingEngine(), trailingStopPercent: percent);
}
