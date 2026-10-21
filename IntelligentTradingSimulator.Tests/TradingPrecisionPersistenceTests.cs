using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class TradingPrecisionPersistenceTests
{
    [Fact]
    public void SqlServerModel_UsesPrecisionPolicyForExecutionAndStateSnapshots()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);

        AssertPrecision<Portfolio>(context, nameof(Portfolio.Cash), 28, 8);
        AssertPrecision<Portfolio>(context, nameof(Portfolio.StartingCash), 28, 8);
        AssertPrecision<Position>(context, nameof(Position.Quantity), 28, 8);
        AssertPrecision<Position>(context, nameof(Position.AveragePrice), 28, 12);
        AssertPrecision<Position>(context, nameof(Position.CurrentPrice), 28, 12);
        AssertPrecision<Position>(context, nameof(Position.HighestPriceSinceEntry), 28, 12);
        AssertPrecision<Trade>(context, nameof(Trade.Quantity), 28, 8);
        AssertPrecision<Trade>(context, nameof(Trade.Price), 28, 12);
        AssertPrecision<PortfolioSnapshot>(context, nameof(PortfolioSnapshot.PortfolioValue), 28, 8);
        AssertPrecision<PortfolioSnapshot>(context, nameof(PortfolioSnapshot.Cash), 28, 8);
        AssertPrecision<PortfolioSnapshot>(context, nameof(PortfolioSnapshot.ProfitLoss), 28, 8);
        AssertPrecision<PortfolioSnapshot>(context, nameof(PortfolioSnapshot.ReturnPercent), 18, 8);
        AssertPrecision<PositionSnapshot>(context, nameof(PositionSnapshot.Quantity), 28, 8);
        AssertPrecision<PositionSnapshot>(context, nameof(PositionSnapshot.CurrentPrice), 28, 12);
        AssertPrecision<PositionSnapshot>(context, nameof(PositionSnapshot.PositionValue), 28, 8);
        AssertPrecision<PositionSnapshot>(context, nameof(PositionSnapshot.Weight), 18, 8);
    }

    [Fact]
    public void PrecisionMigration_AltersOnlyAffectedDecimalColumns_AndGeneratesSqlOffline()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        var migration = new AlignTradeExecutionPrecision();
        var alterations = migration.UpOperations.OfType<AlterColumnOperation>().ToArray();

        Assert.Equal(16, alterations.Length);
        Assert.DoesNotContain(migration.UpOperations,
            operation => operation is DropColumnOperation or DropTableOperation);
        Assert.All(alterations, alteration =>
            Assert.StartsWith("decimal(", alteration.ColumnType, StringComparison.Ordinal));

        var sqlGenerator = context.GetService<IMigrationsSqlGenerator>();
        var sql = string.Join(Environment.NewLine,
            sqlGenerator.Generate(migration.UpOperations, context.Model).Select(command => command.CommandText));
        Assert.Contains("ALTER TABLE [Trades] ALTER COLUMN [Price] decimal(28,12) NOT NULL", sql);
        Assert.Contains("ALTER TABLE [Portfolios] ALTER COLUMN [Cash] decimal(28,8) NOT NULL", sql);
        Assert.Contains("ALTER TABLE [Positions] ALTER COLUMN [Quantity] decimal(28,8) NOT NULL", sql);
        Assert.Contains("ALTER TABLE [PositionSnapshots] ALTER COLUMN [CurrentPrice] decimal(28,12) NOT NULL", sql);
    }

    [Fact]
    public async Task FreshContext_ReloadAndContinuedExecution_ReconcileAtExecutionPrecision()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var uninterrupted = new Portfolio { StartingCash = 1_000m, Cash = 1_000m };
        var persisted = new Portfolio { StartingCash = 1_000m, Cash = 1_000m };
        var engine = new TradingEngine();
        var buy = new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 100m };
        engine.Execute(uninterrupted, buy, 3.123456789123m);
        engine.Execute(persisted, buy, 3.123456789123m);

        await using (var writeContext = new TradingSimulatorDbContext(options))
        {
            await new PortfolioRepository(writeContext).SaveAsync(persisted);
        }

        await using var readContext = new TradingSimulatorDbContext(options);
        var reloaded = await new PortfolioRepository(readContext).GetLatestAsync();
        Assert.NotNull(reloaded);
        Assert.Equal(persisted.Cash, reloaded.Cash);
        Assert.Equal(persisted.Positions.Single().Quantity, reloaded.Positions.Single().Quantity);
        Assert.Equal(persisted.Trades.Single().Price, reloaded.Trades.Single().Price);

        engine.Execute(uninterrupted, buy, 6.123456789123m);
        engine.Execute(reloaded, buy, 6.123456789123m);

        Assert.Equal(uninterrupted.Cash, reloaded.Cash);
        Assert.Equal(uninterrupted.Positions.Single().Quantity, reloaded.Positions.Single().Quantity);
        Assert.Equal(uninterrupted.Positions.Single().AveragePrice, reloaded.Positions.Single().AveragePrice);
        Assert.Equal(
            uninterrupted.StartingCash + uninterrupted.Trades.Sum(SignedExecutedNotional),
            uninterrupted.Cash);
        Assert.Equal(
            reloaded.StartingCash + reloaded.Trades.Sum(SignedExecutedNotional),
            reloaded.Cash);
    }

    private static void AssertPrecision<TEntity>(
        TradingSimulatorDbContext context, string propertyName, int precision, int scale)
        where TEntity : class
    {
        var property = context.Model.FindEntityType(typeof(TEntity))!.FindProperty(propertyName)!;
        Assert.Equal(precision, property.GetPrecision());
        Assert.Equal(scale, property.GetScale());
    }

    private static decimal SignedExecutedNotional(Trade trade)
    {
        var notional = Math.Round(trade.Quantity * trade.Price,
            TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero);
        return trade.Action == TradeActionType.Buy ? -notional : notional;
    }
}
