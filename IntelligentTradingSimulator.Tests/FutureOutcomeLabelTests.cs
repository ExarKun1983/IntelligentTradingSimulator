using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Training;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

public sealed class FutureOutcomeLabelTests
{
    private static readonly DateOnly Date = new(2024, 1, 5);

    private static FeatureSnapshot Snapshot(int id = 1) => new()
    {
        Id = id, Symbol = "AAPL", AnalysisDate = Date, StrategyId = id == 1 ? "test" : "other",
        StrategyConfigurationJson = "{}", Score = 42m,
        Features = new StockRankingFeatures
        {
            Symbol = "AAPL", AnalysisDate = Date, MomentumPercent = 1m,
            AverageVolume = 100m, VolumeTrendPercent = 2m, VolatilityPercent = 3m,
            IsAboveSma5 = true
        }
    };

    private static List<MarketCandle> Prices(int futureSessions = 60, decimal increment = 1m)
    {
        var prices = new List<MarketCandle>();
        var day = Date;
        for (var i = 0; i <= futureSessions; i++)
        {
            prices.Add(new MarketCandle
            {
                Date = day.ToDateTime(TimeOnly.MinValue), Close = 100m + i * increment
            });
            do { day = day.AddDays(1); }
            while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        }
        return prices;
    }

    private static DateOnly LastDate(IReadOnlyList<MarketCandle> prices) =>
        DateOnly.FromDateTime(prices[^1].Date);

    [Fact]
    public void Calculate_DefaultHorizons_CountTradingSessions_AndSortInput()
    {
        var prices = Prices();
        var labels = FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices.AsEnumerable().Reverse().ToArray(), Prices(increment: 0.5m), LastDate(prices));
        Assert.Equal(new[] { 5, 20, 60 }, labels.Select(x => x.HorizonTradingDays));
        foreach (var label in labels)
        {
            Assert.Equal(1, label.FeatureSnapshotId);
            Assert.Equal(DateOnly.FromDateTime(prices[label.HorizonTradingDays].Date), label.OutcomeDate);
            Assert.Equal((decimal)label.HorizonTradingDays, label.ReturnPercent);
            Assert.Equal(label.HorizonTradingDays * 0.5m, label.BenchmarkReturnPercent);
            Assert.True(label.OutperformedBenchmark);
        }
        Assert.Equal(new DateOnly(2024, 1, 12), labels[0].OutcomeDate);
    }

    [Theory]
    [InlineData(-2, -1, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, -1, true)]
    public void Calculate_ReturnsAndComparison_AreSignedAndStrict(int stockIncrement, int benchmarkIncrement, bool expected)
    {
        var prices = Prices(5, stockIncrement);
        var label = Assert.Single(FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices, Prices(5, benchmarkIncrement), LastDate(prices), " spy ", [5]));
        Assert.Equal(stockIncrement * 5m, label.ReturnPercent);
        Assert.Equal(benchmarkIncrement * 5m, label.BenchmarkReturnPercent);
        Assert.Equal(expected, label.OutperformedBenchmark);
        Assert.Equal("SPY", label.BenchmarkSymbol);
    }

    [Fact]
    public void Calculate_CustomHorizons_AreDistinct_AndCutoffPreventsPrematureLabels()
    {
        var prices = Prices();
        var cutoff = DateOnly.FromDateTime(prices[5].Date);
        var labels = FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices, prices, cutoff, horizons: [20, 5, 5, 1]);
        Assert.Equal(new[] { 1, 5 }, labels.Select(x => x.HorizonTradingDays));
        Assert.All(labels, label => Assert.True(label.OutcomeDate <= cutoff));
        Assert.Empty(FutureOutcomeLabelCalculator.Calculate(Snapshot(), prices, prices, Date));
        Assert.Empty(FutureOutcomeLabelCalculator.Calculate(Snapshot(), [], [], cutoff));
        Assert.Empty(FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices.Skip(1).ToArray(), prices, cutoff));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Calculate_RejectsNonPositiveHorizons(int horizon)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), Prices(), Prices(), Date.AddYears(1), horizons: [horizon]));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public void Calculate_InvalidStockEndpoint_IsNotLabeled(int index, int close)
    {
        var prices = Prices(5);
        prices[index].Close = close;
        Assert.Empty(FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices, [], LastDate(prices)));
    }

    [Fact]
    public void Calculate_UnrepresentableReturn_IsNotLabeled()
    {
        var prices = Prices(5);
        prices[0].Close = 0.0000000000000000000000000001m;
        prices[5].Close = decimal.MaxValue;
        Assert.Empty(FutureOutcomeLabelCalculator.Calculate(Snapshot(), prices, [], LastDate(prices)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public void Calculate_MissingOrInvalidBenchmarkEndpoint_DoesNotSubstituteNearbyDate(int index, bool remove)
    {
        var prices = Prices(5);
        var benchmark = Prices(6);
        if (remove)
            benchmark.RemoveAt(index);
        else
            benchmark[index].Close = 0;
        var label = Assert.Single(FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices, benchmark, LastDate(benchmark)));
        Assert.Equal(5m, label.ReturnPercent);
        Assert.Null(label.BenchmarkReturnPercent);
        Assert.Null(label.OutperformedBenchmark);
    }

    [Fact]
    public void Calculate_DuplicateSessionDates_AreRejected()
    {
        var prices = Prices(5);
        prices.Add(prices[1]);
        Assert.Throws<ArgumentException>(() => FutureOutcomeLabelCalculator.Calculate(
            Snapshot(), prices, [], Date.AddYears(1)));
    }

    [Fact]
    public async Task Workflow_GeneratesAndPersistsOffline_WithoutChangingSnapshots()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var prices = Prices(20);
        var source = Snapshot();
        var before = JsonSerializer.Serialize(source);
        await using (var context = new TradingSimulatorDbContext(options))
        {
            context.FeatureSnapshots.AddRange(source, Snapshot(2));
            await context.SaveChangesAsync();
            var repository = new FutureOutcomeLabelRepository(context);
            var marketData = new HistoricalData(prices);
            var workflow = new FutureOutcomeLabelWorkflow(new FeatureSnapshotRepository(context), repository, marketData);
            await workflow.GenerateAsync(Date, Date, LastDate(prices));
            await workflow.GenerateAsync(Date, Date, LastDate(prices));
            await workflow.GenerateAsync(Date, Date, LastDate(prices), "QQQ", [5]);
            Assert.Equal(6, await context.FutureOutcomeLabels.CountAsync());
            Assert.Equal(before, JsonSerializer.Serialize(source));
            Assert.All(marketData.Requests, request =>
            {
                Assert.Equal(Date.ToDateTime(TimeOnly.MinValue), request.From);
                Assert.Equal(LastDate(prices).ToDateTime(TimeOnly.MinValue), request.To);
            });
            var requestsBefore = marketData.Requests.Count;
            await workflow.GenerateAsync(Date, Date, LastDate(prices), strategyId: "missing");
            Assert.Equal(requestsBefore, marketData.Requests.Count);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                workflow.GenerateAsync(Date, Date.AddDays(1), Date));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                workflow.GenerateAsync(Date, Date, LastDate(prices), horizons: [0]));
        }

        await using var readContext = new TradingSimulatorDbContext(options);
        var readRepository = new FutureOutcomeLabelRepository(readContext);
        Assert.Equal(3, (await readRepository.QueryAsync(1)).Count);
        Assert.Equal(2, (await readRepository.QueryAsync(1, 5)).Count);
        var saved = Assert.Single(await readRepository.QueryAsync(1, 5, " spy "));
        Assert.Equal(5m, saved.ReturnPercent);
        Assert.Equal(5m, saved.BenchmarkReturnPercent);
        Assert.False(saved.OutperformedBenchmark);
        Assert.Empty(await readRepository.QueryAsync(999));
        Assert.Empty(readContext.ChangeTracker.Entries<FutureOutcomeLabel>());
        Assert.Equal(before, JsonSerializer.Serialize(await readContext.FeatureSnapshots.SingleAsync(x => x.Id == 1)));
    }

    [Fact]
    public async Task Workflow_LaterCutoffAddsMatureHorizons_AndFiltersSnapshotContext()
    {
        await using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        context.FeatureSnapshots.AddRange(Snapshot(), Snapshot(2));
        await context.SaveChangesAsync();
        var prices = Prices();
        var repository = new FutureOutcomeLabelRepository(context);
        var workflow = new FutureOutcomeLabelWorkflow(
            new FeatureSnapshotRepository(context), repository, new HistoricalData(prices));
        await workflow.GenerateAsync(Date, Date, DateOnly.FromDateTime(prices[4].Date),
            symbol: " aapl ", strategyId: "test");
        Assert.Empty(await repository.QueryAsync(1));
        await workflow.GenerateAsync(Date, Date, DateOnly.FromDateTime(prices[5].Date),
            symbol: " aapl ", strategyId: "test");
        var first = Assert.Single(await repository.QueryAsync(1));
        await workflow.GenerateAsync(Date, Date, LastDate(prices), strategyId: "test");
        var labels = await repository.QueryAsync(1);
        Assert.Equal(new[] { 5, 20, 60 }, labels.Select(x => x.HorizonTradingDays));
        Assert.Equal(first.Id, labels[0].Id);
        Assert.Empty(await repository.QueryAsync(2));
    }

    [Fact]
    public void SqlServerMappingAndMigration_KeepLabelsSeparateAndUniquelyLinked()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        var entity = context.Model.FindEntityType(typeof(FutureOutcomeLabel))!;
        var index = Assert.Single(entity.GetIndexes());
        Assert.True(index.IsUnique);
        Assert.Equal(new[] { "FeatureSnapshotId", "HorizonTradingDays", "BenchmarkSymbol" },
            index.Properties.Select(x => x.Name));
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(typeof(FeatureSnapshot), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
        Assert.Empty(context.Model.FindEntityType(typeof(FeatureSnapshot))!.GetNavigations());
        Assert.Contains("[FeatureSnapshotId]", context.FutureOutcomeLabels
            .Where(x => x.FeatureSnapshotId == 1 && x.HorizonTradingDays == 5).ToQueryString());
        Assert.Contains("CREATE TABLE [FutureOutcomeLabels]", context.Database.GenerateCreateScript());

        var migration = new AddFutureOutcomeLabels();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("FutureOutcomeLabels", table.Name);
        Assert.Equal("FeatureSnapshots", Assert.Single(table.ForeignKeys).PrincipalTable);
        Assert.True(Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>()).IsUnique);
        Assert.Equal("FutureOutcomeLabels", Assert.IsType<DropTableOperation>(
            Assert.Single(migration.DownOperations)).Name);
    }

    private sealed class HistoricalData(IReadOnlyList<MarketCandle> prices) : IMarketDataProvider
    {
        public List<(string Symbol, DateTime From, DateTime To)> Requests { get; } = [];

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from) =>
            throw new InvalidOperationException("Offline labeling must request a bounded historical range.");

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to)
        {
            Requests.Add((symbol, from, to));
            return Task.FromResult(prices);
        }
    }
}
