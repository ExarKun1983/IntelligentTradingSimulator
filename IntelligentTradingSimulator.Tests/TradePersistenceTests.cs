using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class TradePersistenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(ExitReason.Manual)]
    [InlineData(ExitReason.Rebalance)]
    [InlineData(ExitReason.StopLoss)]
    [InlineData(ExitReason.TrailingStop)]
    public async Task SellExitReason_IsPersistedAndQueryable(ExitReason? reason)
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var portfolio = new Portfolio
        {
            Trades =
            [
                new Trade
                {
                    Timestamp = new DateTime(2025, 1, 15),
                    Symbol = "AAPL",
                    Action = TradeActionType.Sell,
                    Quantity = 5m,
                    Price = 100m,
                    ExitReason = reason
                },
                new Trade
                {
                    Timestamp = new DateTime(2025, 1, 14),
                    Symbol = "MSFT",
                    Action = TradeActionType.Buy,
                    Quantity = 10m,
                    Price = 50m
                }
            ]
        };

        await using (var writeContext = new TradingSimulatorDbContext(options))
        {
            await new PortfolioRepository(writeContext).SaveAsync(portfolio);
        }

        await using var readContext = new TradingSimulatorDbContext(options);
        var loaded = await new PortfolioRepository(readContext).GetLatestAsync();

        Assert.NotNull(loaded);
        var sell = Assert.Single(loaded.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(reason, sell.ExitReason);
        Assert.Equal(5m, sell.Quantity);
        Assert.Equal(100m, sell.Price);
        Assert.Null(Assert.Single(loaded.Trades, x => x.Action == TradeActionType.Buy).ExitReason);

        var matches = await readContext.Trades
            .Where(x => x.Action == TradeActionType.Sell && x.ExitReason == reason)
            .AsNoTracking()
            .ToListAsync();

        Assert.Equal(sell.Id, Assert.Single(matches).Id);
    }

    [Fact]
    public void ExitReasonMigration_AddsOnlyNullableColumn_AndCanBeReversed()
    {
        var migration = new AddTradeExitReason();

        var added = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("Trades", added.Table);
        Assert.Equal("ExitReason", added.Name);
        Assert.Equal(typeof(int), added.ClrType);
        Assert.True(added.IsNullable);
        Assert.Null(added.DefaultValue);

        var removed = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("Trades", removed.Table);
        Assert.Equal("ExitReason", removed.Name);
    }

    [Fact]
    public void BacktestPortfolioMigration_AddsNullableLink_AndCanBeReversed()
    {
        var migration = new AddBacktestPortfolio();

        var added = Assert.IsType<AddColumnOperation>(migration.UpOperations[0]);
        Assert.Equal("BacktestRuns", added.Table);
        Assert.Equal("PortfolioId", added.Name);
        Assert.True(added.IsNullable);
        Assert.Null(added.DefaultValue);
        var index = Assert.IsType<CreateIndexOperation>(migration.UpOperations[1]);
        Assert.Equal("PortfolioId", Assert.Single(index.Columns));
        var foreignKey = Assert.IsType<AddForeignKeyOperation>(migration.UpOperations[2]);
        Assert.Equal("BacktestRuns", foreignKey.Table);
        Assert.Equal("Portfolios", foreignKey.PrincipalTable);
        Assert.Equal("PortfolioId", Assert.Single(foreignKey.Columns));
        Assert.Equal("Id", Assert.Single(foreignKey.PrincipalColumns!));
        Assert.Equal(3, migration.UpOperations.Count);

        Assert.IsType<DropForeignKeyOperation>(migration.DownOperations[0]);
        var deleteBacktestPortfolios =
            Assert.IsType<SqlOperation>(migration.DownOperations[1]);
        Assert.Contains("[Portfolios]", deleteBacktestPortfolios.Sql);
        Assert.Contains("[BacktestRuns]", deleteBacktestPortfolios.Sql);
        Assert.Contains("[PortfolioId]", deleteBacktestPortfolios.Sql);
        Assert.IsType<DropIndexOperation>(migration.DownOperations[2]);
        var removed = Assert.IsType<DropColumnOperation>(migration.DownOperations[3]);
        Assert.Equal("BacktestRuns", removed.Table);
        Assert.Equal("PortfolioId", removed.Name);
        Assert.Equal(4, migration.DownOperations.Count);
    }

    [Fact]
    public void ExitReasonFilter_TranslatesToSqlServerQuery()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlServer()
            .Options;
        using var context = new TradingSimulatorDbContext(options);

        var query = context.Trades
            .Where(x => x.Action == TradeActionType.Sell && x.ExitReason == ExitReason.StopLoss)
            .ToQueryString();

        Assert.Contains("WHERE", query);
        Assert.Contains("[ExitReason] = 3", query);
    }
}
