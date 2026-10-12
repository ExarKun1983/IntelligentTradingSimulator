using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class TradePersistenceTests
{
    [Fact]
    public void Migration_AddsNullableExitReasonWithoutReclassifyingExistingTrades()
    {
        var migration = new AddTradeExitReason();

        var addedColumn = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("Trades", addedColumn.Table);
        Assert.Equal("ExitReason", addedColumn.Name);
        Assert.Equal(typeof(int), addedColumn.ClrType);
        Assert.True(addedColumn.IsNullable);
        Assert.Null(addedColumn.DefaultValue);
        Assert.Null(addedColumn.DefaultValueSql);

        var droppedColumn = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("Trades", droppedColumn.Table);
        Assert.Equal("ExitReason", droppedColumn.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExitReason.Manual)]
    [InlineData(ExitReason.Rebalance)]
    [InlineData(ExitReason.StopLoss)]
    [InlineData(ExitReason.TrailingStop)]
    public async Task Sell_ExitReason_CanBeSavedLoadedAndQueried(ExitReason? reason)
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var dbContext = new TradingSimulatorDbContext(options);
        var portfolio = new Portfolio
        {
            Trades =
            [
                new Trade
                {
                    Timestamp = new DateTime(2024, 1, 1),
                    Symbol = "AAPL",
                    Action = TradeActionType.Sell,
                    Quantity = 5m,
                    Price = 100m,
                    ExitReason = reason
                },
                new Trade
                {
                    Timestamp = new DateTime(2024, 1, 1),
                    Symbol = "MSFT",
                    Action = TradeActionType.Buy,
                    Quantity = 1m,
                    Price = 100m
                }
            ]
        };
        await new PortfolioRepository(dbContext).SaveAsync(portfolio);

        await using var readContext = new TradingSimulatorDbContext(options);
        var loaded = await new PortfolioRepository(readContext).GetLatestAsync();

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Trades.Count);
        var sell = Assert.Single(loaded.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(reason, sell.ExitReason);
        Assert.Null(Assert.Single(loaded.Trades, x => x.Action == TradeActionType.Buy).ExitReason);

        var matchingTrades = await readContext.Trades
            .Where(x => x.Action == TradeActionType.Sell && x.ExitReason == reason)
            .ToListAsync();
        Assert.Equal(sell.Id, Assert.Single(matchingTrades).Id);
    }
}
