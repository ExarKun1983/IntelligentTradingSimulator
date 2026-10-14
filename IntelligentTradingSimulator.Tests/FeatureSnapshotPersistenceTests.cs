using System.Text.Json;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class FeatureSnapshotPersistenceTests
{
    private static readonly DateOnly Date = new(2024, 1, 10);

    private static ObservationResult Observation(string symbol = "AAPL", DateOnly? date = null) => new()
    {
        Symbol = symbol,
        Score = -12.123456789012m,
        Features = new StockRankingFeatures
        {
            Symbol = symbol,
            AnalysisDate = date ?? Date,
            MomentumPercent = -2.5m,
            AverageVolume = 123456m,
            VolumeTrendPercent = 3.5m,
            VolatilityPercent = 1.25m,
            IsAboveSma5 = false,
            Rsi = 52m,
            Atr = 2.125m,
            Week52High = 150m,
            DistanceFromWeek52HighPercent = -5m,
            RelativeStrengthBenchmarkSymbol = "SPY",
            RelativeStrengthPercent = -1.5m,
            Fundamentals = new FundamentalData
            {
                Symbol = symbol, AsOfDate = Date.AddDays(-10),
                RevenueGrowthPercent = 10m, EpsGrowthPercent = -2m, Margins = 15m, Debt = 0.5m
            }
        },
        FactorContributions = [new("Momentum", -25m, FactorScoreStatus.Applied)]
    };

    private static DbContextOptions<TradingSimulatorDbContext> Options() =>
        new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    [Fact]
    public async Task SaveAsync_RoundTripsFeaturesScoreAndExplanation_InFreshContext()
    {
        var options = Options();
        var observation = Observation();
        var strategy = new StrategyConfiguration { Id = "weighted", MomentumScoreWeight = 3m };
        await using (var context = new TradingSimulatorDbContext(options))
        {
            await new FeatureSnapshotRepository(context).SaveAsync([observation], strategy);
        }

        await using var readContext = new TradingSimulatorDbContext(options);
        var saved = Assert.Single(await new FeatureSnapshotRepository(readContext)
            .QueryAsync(Date, Date, " aapl ", "weighted"));
        Assert.Equal("AAPL", saved.Symbol);
        Assert.Equal(Date, saved.AnalysisDate);
        Assert.Null(saved.BacktestRunId);
        Assert.Equal(1, saved.SchemaVersion);
        Assert.Equal(observation.Score, saved.Score);
        Assert.Equal(JsonSerializer.Serialize(observation.Features), JsonSerializer.Serialize(saved.Features));
        Assert.Equal(3m, JsonSerializer.Deserialize<StrategyConfiguration>(saved.StrategyConfigurationJson)!.MomentumScoreWeight);
        Assert.Equal(observation.FactorContributions,
            JsonSerializer.Deserialize<List<FactorScoreContribution>>(saved.FactorContributionsJson!));
        Assert.Empty(readContext.ChangeTracker.Entries<FeatureSnapshot>());
    }

    [Fact]
    public async Task SaveAsync_IsIdempotent_AndSeparatesDatesStocksStrategiesAndRuns()
    {
        var options = Options();
        await using var context = new TradingSimulatorDbContext(options);
        var repository = new FeatureSnapshotRepository(context);
        var strategy = new StrategyConfiguration { Id = "one" };
        var first = Observation();
        await repository.SaveAsync([first, first], strategy);
        var changed = Observation("aapl");
        changed.Score = 999m;
        await repository.SaveAsync([changed], strategy);
        await repository.SaveAsync([Observation(date: Date.AddDays(1)), Observation("MSFT")], strategy);
        await repository.SaveAsync([Observation()], new StrategyConfiguration { Id = "two" });
        await repository.SaveAsync([Observation()], strategy, 1);
        await repository.SaveAsync([Observation()], strategy, 2);

        var all = await repository.QueryAsync(Date, Date.AddDays(1));
        Assert.Equal(6, all.Count);
        Assert.Equal(first.Score, Assert.Single(all, x =>
            x.Symbol == "AAPL" && x.AnalysisDate == Date && x.StrategyId == "one" && x.BacktestRunId is null).Score);
        Assert.Single(await repository.QueryAsync(Date, Date, "AAPL", "one", 1));
        Assert.Single(await repository.QueryAsync(Date.AddDays(1), Date.AddDays(1)));
        Assert.Empty(await repository.QueryAsync(Date.AddDays(2), Date.AddDays(3)));
        Assert.Empty(await repository.QueryAsync(Date, Date, strategyId: "missing"));
        Assert.Equal(all.OrderBy(x => x.AnalysisDate).ThenBy(x => x.Symbol)
            .ThenBy(x => x.StrategyId).ThenBy(x => x.BacktestRunId).Select(x => x.Id), all.Select(x => x.Id));
    }

    [Fact]
    public async Task SaveAsync_AddsOnlyMissingSnapshotsFromBatch()
    {
        await using var context = new TradingSimulatorDbContext(Options());
        var repository = new FeatureSnapshotRepository(context);
        var strategy = new StrategyConfiguration { Id = "batch" };
        var original = Observation();
        await repository.SaveAsync([original], strategy);

        var changed = Observation();
        changed.Score = 999m;
        await repository.SaveAsync([changed, Observation("MSFT"), Observation("NVDA")], strategy);

        var saved = await repository.QueryAsync(Date, Date);
        Assert.Equal(3, saved.Count);
        Assert.Equal(original.Score, Assert.Single(saved, x => x.Symbol == "AAPL").Score);
        Assert.Contains(saved, x => x.Symbol == "MSFT");
        Assert.Contains(saved, x => x.Symbol == "NVDA");
    }

    [Fact]
    public async Task SaveAsync_RejectsMissingOrMismatchedFeatures_WithoutPartialWrites()
    {
        await using var context = new TradingSimulatorDbContext(Options());
        var repository = new FeatureSnapshotRepository(context);
        var invalid = Observation();
        invalid.Symbol = "MSFT";
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(
            [Observation(), invalid], new StrategyConfiguration()));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveAsync(
            [new ObservationResult { Symbol = "AAPL" }], new StrategyConfiguration()));
        Assert.Empty(context.FeatureSnapshots);
    }

    [Fact]
    public void SqlServerMapping_RoundTripsOptionalFeatures_AndSupportsDatedQueries()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        var entity = context.Model.FindEntityType(typeof(FeatureSnapshot))!;
        var converter = entity.FindProperty(nameof(FeatureSnapshot.Features))!.GetValueConverter()!;
        var features = new StockRankingFeatures
        {
            Symbol = "AAPL", AnalysisDate = Date, MomentumPercent = 0m,
            AverageVolume = 100m, VolumeTrendPercent = 0m,
            VolatilityPercent = 0m, IsAboveSma5 = false
        };
        var roundTrip = Assert.IsType<StockRankingFeatures>(
            converter.ConvertFromProvider(converter.ConvertToProvider(features)));
        Assert.Equal(JsonSerializer.Serialize(features), JsonSerializer.Serialize(roundTrip));
        Assert.Null(roundTrip.Fundamentals);
        Assert.Null(roundTrip.Rsi);
        Assert.Null(roundTrip.Atr);
        var populated = Observation().Features!;
        Assert.Equal(JsonSerializer.Serialize(populated), JsonSerializer.Serialize(
            converter.ConvertFromProvider(converter.ConvertToProvider(populated))));
        Assert.True(entity.FindProperty(nameof(FeatureSnapshot.Score))!.IsNullable);

        var sql = context.FeatureSnapshots.Where(x =>
            x.AnalysisDate >= Date && x.AnalysisDate <= Date && x.Symbol == "AAPL" &&
            x.StrategyId == "one" && x.BacktestRunId == 1).ToQueryString();
        Assert.Contains("[AnalysisDate]", sql);
        Assert.Contains("[StrategyId]", sql);
        Assert.Contains("[BacktestRunId]", sql);
        var ddl = context.Database.GenerateCreateScript();
        Assert.Contains("WHERE [BacktestRunId] IS NULL", ddl);
        Assert.Contains("WHERE [BacktestRunId] IS NOT NULL", ddl);
        Assert.Contains("CREATE UNIQUE INDEX", ddl);
    }

    [Fact]
    public async Task SaveAsync_RejectsFutureFundamentals()
    {
        await using var context = new TradingSimulatorDbContext(Options());
        await Assert.ThrowsAsync<ArgumentException>(() => new FeatureSnapshotRepository(context)
            .SaveAsync([Observation(date: Date.AddDays(-11))], new StrategyConfiguration()));
        Assert.Empty(context.FeatureSnapshots);
    }

    [Fact]
    public void Migration_AddsOnlySnapshotTable_WithUniqueContexts_AndCanBeReversed()
    {
        var migration = new AddFeatureSnapshots();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("FeatureSnapshots", table.Name);
        Assert.Equal(typeof(DateOnly), Assert.Single(table.Columns, x => x.Name == "AnalysisDate").ClrType);
        Assert.Equal("BacktestRuns", Assert.Single(table.ForeignKeys).PrincipalTable);
        var uniqueIndexes = migration.UpOperations.OfType<CreateIndexOperation>().Where(x => x.IsUnique).ToList();
        Assert.Equal(2, uniqueIndexes.Count);
        Assert.Contains(uniqueIndexes, x => x.Filter == "[BacktestRunId] IS NULL" &&
            x.Columns.SequenceEqual(["Symbol", "AnalysisDate", "StrategyId"]));
        Assert.Contains(uniqueIndexes, x => x.Filter == "[BacktestRunId] IS NOT NULL" &&
            x.Columns.SequenceEqual(["Symbol", "AnalysisDate", "StrategyId", "BacktestRunId"]));
        Assert.Equal("FeatureSnapshots", Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
    }
}
