using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class PositionTargetBaselineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstSelectedTarget_InitializesCappedBaselineWithoutSale_AndSurvivesFreshContexts(bool sharedConnection)
    {
        await using var database = await TestDatabase.Create(sharedConnection);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m,
            Positions = [Holding("AAA", 10m)]
        });
        Assert.Null(Assert.Single((await database.Load()).Positions).EffectiveTargetPercent);

        await database.Run([Target("AAA", 100m)], Prices(100m), cap: 25m);
        var initialized = await database.Load();
        Assert.Equal(25m, Assert.Single(initialized.Positions).EffectiveTargetPercent);
        Assert.Equal(10m, initialized.Positions[0].Quantity);
        Assert.Empty(initialized.Trades);

        await database.Run([Target("AAA", 80m), Target("BBB", 20m)], Prices(200m), cap: 25m);
        var drifted = await database.Load();
        Assert.Equal(25m, Assert.Single(drifted.Positions).EffectiveTargetPercent);
        Assert.Equal(10m, drifted.Positions[0].Quantity);
        Assert.Empty(drifted.Trades);
        Assert.Equal(0m, drifted.Cash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedEffectiveReduction_PersistsWithTrade_AndDoesNotRepeatAfterPriceDrift(bool sharedConnection)
    {
        await using var database = await TestDatabase.Create(sharedConnection);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m,
            Positions = [Holding("AAA", 5m), Holding("BBB", 5m)]
        });
        await database.Run([Target("AAA", 1m), Target("BBB", 1m)], Prices(100m));
        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], Prices(100m));

        var reduced = await database.Load();
        Assert.Equal(25m, reduced.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
        Assert.Equal(2.5m, reduced.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Equal(75m, reduced.Positions.Single(x => x.Symbol == "BBB").EffectiveTargetPercent);
        var sale = Assert.Single(reduced.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(ExitReason.Rebalance, sale.ExitReason);
        Assert.Equal(2.5m, sale.Quantity);
        Assert.Equal(0m, reduced.Cash);

        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], Prices(200m));
        var drifted = await database.Load();
        Assert.Equal(reduced.Trades.Count, drifted.Trades.Count);
        Assert.Equal(2.5m, drifted.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Equal(25m, drifted.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BelowThresholdReduction_PreservesBaselineUntilItCanExecute(bool sharedConnection)
    {
        await using var database = await TestDatabase.Create(sharedConnection);
        await database.Seed(new Portfolio
        {
            StartingCash = 100m,
            Positions = [Holding("AAA", 0.5m, 50m), Holding("BBB", 0.5m, 50m)]
        });

        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], Prices(100m));
        var unfilled = await database.Load();
        Assert.Empty(unfilled.Trades);
        Assert.Equal(50m, unfilled.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);

        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], Prices(1000m, 1000m));
        var filled = await database.Load();
        Assert.Equal(25m, filled.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
        Assert.Equal(0.25m, filled.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Single(filled.Trades, x => x.Action == TradeActionType.Sell);
    }

    [Fact]
    public async Task RejectedSubQuantumReduction_PreservesBaselineAcrossContexts()
    {
        await using var database = await TestDatabase.Create(true);
        var aaa = Holding("AAA", 0.00000001m, 50m);
        aaa.CurrentPrice = aaa.AveragePrice = 1_000_000_000_000m;
        var bbb = Holding("BBB", 1m, 50m);
        bbb.CurrentPrice = bbb.AveragePrice = 10_000m;
        await database.Seed(new Portfolio { StartingCash = 20_000m, Positions = [aaa, bbb] });
        var prices = Prices(1_000_000_000_000m, 10_000m);

        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], prices);
        var rejected = await database.Load();
        Assert.Empty(rejected.Trades);
        Assert.Equal(0.00000001m, rejected.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Equal(50m, rejected.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);

        await database.Run([Target("AAA", 1m), Target("BBB", 3m)], prices);
        Assert.Empty((await database.Load()).Trades);
        Assert.Equal(50m, (await database.Load()).Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
    }

    [Fact]
    public async Task RoundedPartialPositionSale_CompletesWithinShareQuantum_WithoutRepeatedDriftSale()
    {
        await using var database = await TestDatabase.Create(true);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m,
            Positions = [Holding("AAA", 5m, 50m), Holding("BBB", 5m, 50m)]
        });
        const decimal price = 123.456789123m;
        var expectedSale = TradingPrecisionPolicy.FloorQuantity(
            (5m * price - (5m * price + 500m) * 33.33m / 100m) / price);
        await database.Run([Target("AAA", 1m), Target("BBB", 2m)], Prices(price));
        var filled = await database.Load();
        var sale = Assert.Single(filled.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(expectedSale, sale.Quantity);
        Assert.InRange(sale.Quantity, 0.00000001m, 4.99999999m);
        Assert.Equal(5m - expectedSale, filled.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Equal(33.33m, filled.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
        await database.Run([Target("AAA", 1m), Target("BBB", 2m)], Prices(200m));
        Assert.Equal(filled.Trades.Count, (await database.Load()).Trades.Count);
    }

    [Theory]
    [InlineData("omitted")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("rounded-zero")]
    public async Task UnsignaledHolding_PreservesKnownBaseline(string scenario)
    {
        await using var database = await TestDatabase.Create(true);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m,
            Positions = [Holding("AAA", 5m, 50m), Holding("BBB", 5m, 50m)]
        });
        IReadOnlyList<StockRecommendation> targets = scenario switch
        {
            "omitted" => [Target("BBB", 1m)],
            "rounded-zero" => [Target("AAA", 0.001m), Target("BBB", 100m)],
            _ => [Target("AAA", 1m), Target("BBB", 3m)]
        };
        var prices = Prices(100m);
        if (scenario == "missing")
            prices.Remove("AAA");
        if (scenario == "invalid")
            prices["AAA"] = 0m;

        if (scenario is "missing" or "invalid")
            await Assert.ThrowsAsync<InvalidOperationException>(() => database.Run(targets, prices));
        else
            await database.Run(targets, prices);

        var saved = await database.Load();
        Assert.Equal(50m, saved.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
        Assert.Equal(5m, saved.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.DoesNotContain(saved.Trades, x => x.Symbol == "AAA");
    }

    [Fact]
    public async Task CashFundedTopUp_RespectsCapAndCapacity_AndPersistsBaseline()
    {
        await using var database = await TestDatabase.Create(true);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m, Cash = 600m,
            Positions = [Holding("AAA", 4m)]
        });

        await database.Run([Target("AAA", 10m), Target("BBB", 9m)], Prices(100m), cap: 60m, limit: 1);

        var saved = await database.Load();
        var position = Assert.Single(saved.Positions);
        Assert.Equal("AAA", position.Symbol);
        Assert.Equal(6m, position.Quantity);
        Assert.Equal(60m, position.EffectiveTargetPercent);
        Assert.Equal(400m, saved.Cash);
        Assert.Equal(TradeActionType.Buy, Assert.Single(saved.Trades).Action);
    }

    [Fact]
    public async Task NewFilledPosition_PersistsCappedBaseline_AndDoesNotSellPriceAppreciation()
    {
        await using var database = await TestDatabase.Create(true);
        await database.Seed(new Portfolio { StartingCash = 1000m, Cash = 1000m });

        await database.Run([Target("AAA", 100m)], Prices(100m), cap: 25m, limit: 1);
        var purchased = await database.Load();
        var position = Assert.Single(purchased.Positions);
        Assert.Equal(25m, position.EffectiveTargetPercent);
        Assert.Equal(2.5m, position.Quantity);
        Assert.Equal(750m, purchased.Cash);

        await database.Run([Target("AAA", 100m)], Prices(200m), cap: 25m, limit: 1);
        var appreciated = await database.Load();
        Assert.Equal(TradeActionType.Buy, Assert.Single(appreciated.Trades).Action);
        Assert.Equal(2.5m, Assert.Single(appreciated.Positions).Quantity);
        Assert.Equal(25m, appreciated.Positions[0].EffectiveTargetPercent);
        Assert.Equal(750m, appreciated.Cash);
    }

    [Fact]
    public async Task FailedRelationalSave_DoesNotPersistReductionBaselineOrTrades()
    {
        await using var database = await TestDatabase.Create(true);
        await database.Seed(new Portfolio
        {
            StartingCash = 1000m,
            Positions = [Holding("AAA", 5m, 50m), Holding("BBB", 5m, 50m)]
        });
        await using (var context = database.Context())
        {
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER RejectTrade BEFORE INSERT ON Trades BEGIN SELECT RAISE(ABORT, 'audit rejection'); END;");
        }

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            database.Run([Target("AAA", 1m), Target("BBB", 3m)], Prices(100m)));

        var saved = await database.Load();
        Assert.Empty(saved.Trades);
        Assert.Equal(0m, saved.Cash);
        Assert.All(saved.Positions, x =>
        {
            Assert.Equal(5m, x.Quantity);
            Assert.Equal(50m, x.EffectiveTargetPercent);
        });
    }

    [Fact]
    public async Task MultipleRoundedSales_AcknowledgeEachCompletedInstructionWithoutDriftRetries()
    {
        await using var database = await TestDatabase.Create(true);
        Position Initial(string symbol, decimal quantity, decimal baseline) => new()
        {
            Symbol = symbol, Quantity = quantity, AveragePrice = 0.01m, CurrentPrice = 0.01m,
            EffectiveTargetPercent = baseline
        };
        await database.Seed(new Portfolio
        {
            StartingCash = 10_000m,
            Positions =
            [
                Initial("AAA", 100000.00000149m, 10m),
                Initial("BBB", 100000.00000149m, 10m),
                Initial("CCC", 750000.00000049m, 75m),
                Initial("DDD", 49999.99999653m, 5m)
            ]
        });
        var targets = new[]
        {
            Target("AAA", 5m), Target("BBB", 5m), Target("CCC", 70m), Target("DDD", 20m)
        };
        var prices = new Dictionary<string, decimal>
        {
            ["AAA"] = 0.01m, ["BBB"] = 0.01m, ["CCC"] = 0.01m, ["DDD"] = 0.01m
        };
        await database.Run(targets, prices, limit: 4);
        var completed = await database.Load();
        Assert.Equal(3, completed.Trades.Count(x => x.Action == TradeActionType.Sell));
        Assert.Equal(5m, completed.Positions.Single(x => x.Symbol == "AAA").EffectiveTargetPercent);
        Assert.Equal(5m, completed.Positions.Single(x => x.Symbol == "BBB").EffectiveTargetPercent);
        Assert.Equal(70m, completed.Positions.Single(x => x.Symbol == "CCC").EffectiveTargetPercent);

        prices["CCC"] = 0.02m;
        await database.Run(targets, prices, limit: 4);
        Assert.Equal(completed.Trades.Count, (await database.Load()).Trades.Count);
    }

    [Fact]
    public void SqlServerMappingAndMigration_AddOnlyNullableBaselineWithoutBackfill()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        var property = context.Model.FindEntityType(typeof(Position))!
            .FindProperty(nameof(Position.EffectiveTargetPercent))!;
        Assert.True(property.IsNullable);
        Assert.Equal(28, property.GetPrecision());
        Assert.Equal(8, property.GetScale());
        Assert.False(context.Database.HasPendingModelChanges());
        var migration = new AddPositionEffectiveTarget();
        var add = Assert.IsType<AddColumnOperation>(Assert.Single(migration.UpOperations));
        Assert.Equal("Positions", add.Table);
        Assert.Equal(nameof(Position.EffectiveTargetPercent), add.Name);
        Assert.True(add.IsNullable);
        Assert.Null(add.DefaultValue);
        Assert.Null(add.DefaultValueSql);
        var sql = string.Join(Environment.NewLine,
            context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, context.Model)
                .Select(x => x.CommandText));
        Assert.Contains("ALTER TABLE [Positions] ADD [EffectiveTargetPercent] decimal(28,8) NULL", sql);
        Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        var drop = Assert.IsType<DropColumnOperation>(Assert.Single(migration.DownOperations));
        Assert.Equal(nameof(Position.EffectiveTargetPercent), drop.Name);
    }

    [Fact]
    public async Task SqliteMigrationOperations_LeaveExistingRowsNull()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(connection).Options);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE Positions (Id INTEGER PRIMARY KEY, Symbol TEXT NOT NULL); INSERT INTO Positions VALUES (1, 'AAA');");
        var migration = new AddPositionEffectiveTarget();
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, context.Model))
        {
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
        }

        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT EffectiveTargetPercent FROM Positions WHERE Id = 1";
        Assert.Equal(DBNull.Value, await query.ExecuteScalarAsync());
    }

    private static Position Holding(string symbol, decimal quantity, decimal? baseline = null) => new()
    {
        Symbol = symbol, Quantity = quantity, CurrentPrice = 100m, AveragePrice = 100m,
        EffectiveTargetPercent = baseline
    };

    private static StockRecommendation Target(string symbol, decimal score) => new() { Symbol = symbol, Score = score };

    private static Dictionary<string, decimal> Prices(decimal aaa, decimal bbb = 100m) =>
        new() { ["AAA"] = aaa, ["BBB"] = bbb };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection? _connection;
        private readonly DbContextOptions<TradingSimulatorDbContext> _options;

        private TestDatabase(SqliteConnection? connection, DbContextOptions<TradingSimulatorDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        public static async Task<TestDatabase> Create(bool sharedConnection)
        {
            var builder = new DbContextOptionsBuilder<TradingSimulatorDbContext>();
            var connectionString = sharedConnection ? "Data Source=:memory:" :
                $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            if (sharedConnection)
                builder.UseSqlite(connection);
            else
                builder.UseSqlite(connectionString);
            var database = new TestDatabase(connection, builder.Options);
            await using var context = database.Context();
            await context.Database.EnsureCreatedAsync();
            return database;
        }

        public TradingSimulatorDbContext Context() => new(_options);

        public async Task Seed(Portfolio portfolio)
        {
            await using var context = Context();
            await new PortfolioRepository(context).SaveAsync(portfolio);
        }

        public async Task<Portfolio> Load()
        {
            await using var context = Context();
            return (await new PortfolioRepository(context).GetLatestAsync())!;
        }

        public async Task Run(IReadOnlyList<StockRecommendation> targets,
            IReadOnlyDictionary<string, decimal> prices, decimal cap = 100m, int limit = 2)
        {
            await using var context = Context();
            var allocationService = new PortfolioAllocationService();
            var configuration = new StrategyConfiguration { MaxPositions = limit, MaxPositionWeight = cap };
            var workflow = new PortfolioWorkflow(
                new PortfolioRepository(context),
                new PortfolioSnapshotRepository(context),
                new PositionSnapshotRepository(context),
                new PortfolioExecutionService(cap, limit), new TradingEngine(),
                new PortfolioAllocationEngine(allocationService, cap), new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                new ConsoleReporter(allocationService, new PortfolioContributionService()), configuration,
                timeProvider: PaperWorkflowTestSupport.Clock);
            await PaperWorkflowTestSupport.RunAsync(workflow, context, targets, prices);
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is not null)
                await _connection.DisposeAsync();
        }
    }
}
