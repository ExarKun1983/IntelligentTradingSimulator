using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class TradePersistenceTests
{
    [Theory]
    [InlineData(ExitReason.Manual)]
    [InlineData(ExitReason.Rebalance)]
    [InlineData(ExitReason.StopLoss)]
    [InlineData(ExitReason.TrailingStop)]
    public async Task SellExitReason_IsPersistedInHistory_AndQueryable(ExitReason reason)
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        int portfolioId;
        using (var context = new TradingSimulatorDbContext(options))
        {
            var portfolio = new Portfolio
            {
                Positions =
                [
                    new Position { Symbol = "AAPL", Quantity = 10m, CurrentPrice = 100m }
                ]
            };
            new TradingEngine().Execute(
                portfolio,
                new TradingDecision
                {
                    Symbol = "AAPL",
                    Action = TradeActionType.Sell,
                    Amount = 500m,
                    ExitReason = reason
                },
                currentPrice: 100m);

            await new PortfolioRepository(context).SaveAsync(portfolio);
            portfolioId = portfolio.Id;
        }

        using var reloadedContext = new TradingSimulatorDbContext(options);
        var loaded = await new PortfolioRepository(reloadedContext).GetLatestAsync();

        Assert.NotNull(loaded);
        Assert.Equal(portfolioId, loaded.Id);
        var trade = Assert.Single(loaded.Trades);
        Assert.Equal(reason, trade.ExitReason);
        Assert.Equal(TradeActionType.Sell, trade.Action);
        Assert.Equal(5m, trade.Quantity);
        var matches = await reloadedContext.Trades
            .Where(x => x.Action == TradeActionType.Sell && x.ExitReason == reason)
            .ToListAsync();
        Assert.Equal(trade.Id, Assert.Single(matches).Id);
    }

    [Theory]
    [InlineData(TradeActionType.Buy)]
    [InlineData(TradeActionType.Sell)]
    public async Task TradeWithoutExitReason_RemainsNullAfterReload(TradeActionType action)
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using (var context = new TradingSimulatorDbContext(options))
        {
            var portfolio = new Portfolio
            {
                Trades =
                [
                    new Trade
                    {
                        Timestamp = new DateTime(2024, 1, 1),
                        Symbol = "AAPL",
                        Action = action,
                        Quantity = 5m,
                        Price = 100m
                    }
                ]
            };
            await new PortfolioRepository(context).SaveAsync(portfolio);
        }

        using var reloadedContext = new TradingSimulatorDbContext(options);
        var loaded = await new PortfolioRepository(reloadedContext).GetLatestAsync();

        Assert.NotNull(loaded);
        Assert.Null(Assert.Single(loaded.Trades).ExitReason);
        Assert.Single(await reloadedContext.Trades
            .Where(x => x.ExitReason == null)
            .ToListAsync());
    }

    [Fact]
    public void ExitReasonMigration_AddsOnlyNullableColumn_WithoutBackfillingHistory()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlServer("Server=localhost;Database=IntelligentTradingSimulator;Integrated Security=True")
            .Options;
        using var context = new TradingSimulatorDbContext(options);
        var assembly = context.GetService<IMigrationsAssembly>();
        var migrationEntry = assembly.Migrations.Single(
            x => x.Key.EndsWith("_AddTradeExitReason", StringComparison.Ordinal));
        var migration = assembly.CreateMigration(
            migrationEntry.Value,
            context.Database.ProviderName!);

        var add = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("Trades", add.Table);
        Assert.Equal("ExitReason", add.Name);
        Assert.Equal(typeof(int), add.ClrType);
        Assert.True(add.IsNullable);
        Assert.Null(add.DefaultValue);
        Assert.Null(add.DefaultValueSql);
        var drop = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal("Trades", drop.Table);
        Assert.Equal("ExitReason", drop.Name);
        Assert.False(context.Database.HasPendingModelChanges());

        var sql = context.GetService<IMigrator>()
            .GenerateScript("20260625230055_Mig11", migrationEntry.Key);
        Assert.Contains("ALTER TABLE [Trades] ADD [ExitReason] int NULL;", sql);
        var query = context.Trades
            .Where(x => x.Action == TradeActionType.Sell && x.ExitReason == ExitReason.StopLoss)
            .ToQueryString();
        Assert.Contains("[ExitReason] = 3", query);
    }
}
