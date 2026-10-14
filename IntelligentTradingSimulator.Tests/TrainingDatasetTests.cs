using System.Globalization;
using System.Text;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Training;
using IntelligentTradingSimulator.Infrastructure;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.Tests;

public class TrainingDatasetTests
{
    private static readonly DateOnly Date = new(2025, 1, 6);

    [Fact]
    public async Task BuildAsync_JoinsPersistedLabelsBySnapshot_AndPreservesFeaturesAndMetadata()
    {
        var options = Options();
        await using (var writeContext = new TradingSimulatorDbContext(options))
            await SeedAsync(writeContext);
        await using var context = new TradingSimulatorDbContext(options);
        var before = JsonSerializer.Serialize(await context.FeatureSnapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync());

        var dataset = await Builder(context).BuildAsync(new TrainingDatasetQuery { IncludeStrategyMetadata = true });

        Assert.Equal(1, dataset.SchemaVersion);
        Assert.Equal(new[] { 1, 1, 1, 2, 3 }, dataset.Rows.Select(x => x.FeatureSnapshotId));
        Assert.Equal(new[] { 5, 5, 20, 5, 5 }, dataset.Rows.Select(x => x.HorizonTradingDays));
        Assert.Equal(new[] { "QQQ", "SPY", "SPY", "SPY", "SPY" }, dataset.Rows.Select(x => x.BenchmarkSymbol));
        var row = dataset.Rows[1];
        Assert.Equal(2, row.FutureOutcomeLabelId);
        Assert.Equal(1, row.FeatureSchemaVersion);
        Assert.Equal(Date, row.AnalysisDate);
        Assert.Equal("AAPL", row.Symbol);
        Assert.Equal("alpha", row.StrategyId);
        Assert.Null(row.BacktestRunId);
        Assert.Equal("{\"Id\":\"alpha\"}", row.StrategyConfigurationJson);
        Assert.Equal(12.25m, row.Features.MomentumPercent);
        Assert.Equal(1000m, row.Features.AverageVolume);
        Assert.Equal(-2m, row.Features.VolumeTrendPercent);
        Assert.Equal(3m, row.Features.VolatilityPercent);
        Assert.True(row.Features.IsAboveSma5);
        Assert.Null(row.Features.Rsi);
        Assert.Equal(1.5m, row.Features.Atr);
        Assert.Equal(200m, row.Features.Week52High);
        Assert.Equal(-10m, row.Features.DistanceFromWeek52HighPercent);
        Assert.Equal("SPY", row.Features.RelativeStrengthBenchmarkSymbol);
        Assert.Equal(4m, row.Features.RelativeStrengthPercent);
        Assert.Equal(8m, row.Features.Fundamentals!.RevenueGrowthPercent);
        Assert.Equal(Date.AddDays(-30), row.Features.Fundamentals.AsOfDate);
        Assert.Equal(Date.AddDays(7), row.OutcomeDate);
        Assert.Equal(7.5m, row.ReturnPercent);
        Assert.Equal(2.5m, row.BenchmarkReturnPercent);
        Assert.Equal(5m, row.RelativeReturnPercent);
        Assert.True(row.OutperformedBenchmark);
        Assert.Equal(-4m, dataset.Rows[2].RelativeReturnPercent);
        Assert.False(dataset.Rows[2].OutperformedBenchmark);
        Assert.Null(dataset.Rows[0].BenchmarkReturnPercent);
        Assert.Null(dataset.Rows[0].RelativeReturnPercent);
        Assert.Null(dataset.Rows[0].OutperformedBenchmark);
        Assert.Equal(before, JsonSerializer.Serialize(await context.FeatureSnapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync()));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("date", 1)]
    [InlineData("strategy", 4)]
    [InlineData("run", 1)]
    [InlineData("symbol", 5)]
    [InlineData("horizon", 1)]
    [InlineData("benchmark", 1)]
    [InlineData("combined", 1)]
    [InlineData("missing", 0)]
    public async Task BuildAsync_AppliesInclusiveDateAndContextFilters(string filter, int count)
    {
        await using var context = new TradingSimulatorDbContext(Options());
        await SeedAsync(context);
        var query = filter switch
        {
            "date" => new TrainingDatasetQuery { From = Date.AddDays(1), To = Date.AddDays(1) },
            "strategy" => new TrainingDatasetQuery { StrategyId = "alpha" },
            "run" => new TrainingDatasetQuery { BacktestRunId = 42 },
            "symbol" => new TrainingDatasetQuery { Symbol = " aapl " },
            "horizon" => new TrainingDatasetQuery { HorizonTradingDays = 20 },
            "benchmark" => new TrainingDatasetQuery { BenchmarkSymbol = " qqq " },
            "combined" => new TrainingDatasetQuery
            {
                From = Date, To = Date, Symbol = "aapl", StrategyId = "alpha",
                BacktestRunId = 42, HorizonTradingDays = 5, BenchmarkSymbol = "spy"
            },
            _ => new TrainingDatasetQuery { StrategyId = "missing" }
        };

        var dataset = await Builder(context).BuildAsync(query);

        Assert.Equal(count, dataset.Rows.Count);
        Assert.All(dataset.Rows, row => Assert.Null(row.StrategyConfigurationJson));
        Assert.Equal(query with
        {
            Symbol = query.Symbol?.Trim().ToUpperInvariant(),
            BenchmarkSymbol = query.BenchmarkSymbol?.Trim().ToUpperInvariant()
        }, dataset.Query);
        if (filter == "combined")
        {
            var row = Assert.Single(dataset.Rows);
            Assert.Equal(2, row.FeatureSnapshotId);
            Assert.Equal(42, row.BacktestRunId);
        }
    }

    [Theory]
    [InlineData("range")]
    [InlineData("zeroHorizon")]
    [InlineData("negativeHorizon")]
    [InlineData("run")]
    [InlineData("symbol")]
    [InlineData("strategy")]
    [InlineData("benchmark")]
    public async Task BuildAsync_RejectsInvalidQuery(string invalid)
    {
        await using var context = new TradingSimulatorDbContext(Options());
        var query = invalid switch
        {
            "range" => new TrainingDatasetQuery { From = Date.AddDays(1), To = Date },
            "zeroHorizon" => new TrainingDatasetQuery { HorizonTradingDays = 0 },
            "negativeHorizon" => new TrainingDatasetQuery { HorizonTradingDays = -5 },
            "run" => new TrainingDatasetQuery { BacktestRunId = 0 },
            "symbol" => new TrainingDatasetQuery { Symbol = " " },
            "strategy" => new TrainingDatasetQuery { StrategyId = "" },
            _ => new TrainingDatasetQuery { BenchmarkSymbol = " " }
        };
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Builder(context).BuildAsync(query));
    }

    [Fact]
    public async Task JsonExport_IsDeterministicCultureIndependentAndRoundTripsWithExplicitNulls()
    {
        await using var context = new TradingSimulatorDbContext(Options());
        await SeedAsync(context);
        var builder = Builder(context);
        var query = new TrainingDatasetQuery { From = Date, To = Date, IncludeStrategyMetadata = true };
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var first = await ExportAsync(await builder.BuildAsync(query));
            var second = await ExportAsync(await builder.BuildAsync(query));
            Assert.Equal(first, second);
            using var json = JsonDocument.Parse(first);
            Assert.Equal(new[] { "schemaVersion", "query", "rows" },
                json.RootElement.EnumerateObject().Select(x => x.Name));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("2025-01-06", json.RootElement.GetProperty("query").GetProperty("from").GetString());
            var row = json.RootElement.GetProperty("rows")[0];
            Assert.Equal(12.25m, row.GetProperty("features").GetProperty("momentumPercent").GetDecimal());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("features").GetProperty("rsi").ValueKind);
            Assert.Equal(JsonValueKind.Null, row.GetProperty("relativeReturnPercent").ValueKind);
            Assert.False(row.GetProperty("features").TryGetProperty("returnPercent", out _));
            var restored = JsonSerializer.Deserialize<TrainingDataset>(first,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
            Assert.Equal(query, restored.Query);
            Assert.Equal(4, restored.Rows.Count);
            Assert.Equal("{\"Id\":\"alpha\"}", restored.Rows[0].StrategyConfigurationJson);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task EmptyDataset_ExportsSchemaAndQueryWithoutInventingLabels()
    {
        await using var context = new TradingSimulatorDbContext(Options());
        await SeedAsync(context);
        var query = new TrainingDatasetQuery { From = Date.AddDays(2), To = Date.AddDays(2) };
        var dataset = await Builder(context).BuildAsync(query);
        Assert.Empty(dataset.Rows);
        using var json = JsonDocument.Parse(await ExportAsync(dataset));
        Assert.Equal(0, json.RootElement.GetProperty("rows").GetArrayLength());
        Assert.Equal("2025-01-08", json.RootElement.GetProperty("query").GetProperty("to").GetString());
    }

    [Fact]
    public void Infrastructure_RegistersOfflineBuilder()
    {
        var services = new ServiceCollection();
        services.AddInfrastructure(string.Empty);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<TrainingDatasetBuilder>());
    }

    private static DbContextOptions<TradingSimulatorDbContext> Options() =>
        new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static TrainingDatasetBuilder Builder(TradingSimulatorDbContext context) =>
        new(new FeatureSnapshotRepository(context), new FutureOutcomeLabelRepository(context));

    private static async Task<string> ExportAsync(TrainingDataset dataset)
    {
        await using var stream = new MemoryStream();
        await TrainingDatasetJsonExporter.ExportAsync(dataset, stream);
        Assert.True(stream.CanWrite);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task SeedAsync(TradingSimulatorDbContext context)
    {
        context.FeatureSnapshots.AddRange(
            Snapshot(4, Date.AddDays(2), "alpha"),
            Snapshot(3, Date.AddDays(1), "beta"),
            Snapshot(2, Date, "alpha", 42),
            Snapshot(1, Date, "alpha"));
        await context.SaveChangesAsync();
        context.FutureOutcomeLabels.AddRange(
            Label(5, 3, 5, "SPY", 2m, 1m),
            Label(4, 2, 5, "SPY", 3m, 1m),
            Label(3, 1, 20, "SPY", -2m, 2m),
            Label(2, 1, 5, "SPY", 7.5m, 2.5m),
            Label(1, 1, 5, "QQQ", 7.5m, null));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static FeatureSnapshot Snapshot(int id, DateOnly date, string strategyId, int? runId = null) => new()
    {
        Id = id, Symbol = "AAPL", AnalysisDate = date, StrategyId = strategyId, BacktestRunId = runId,
        StrategyConfigurationJson = $"{{\"Id\":\"{strategyId}\"}}",
        Features = new StockRankingFeatures
        {
            Symbol = "AAPL", AnalysisDate = date, MomentumPercent = 12.25m, AverageVolume = 1000m,
            VolumeTrendPercent = -2m, VolatilityPercent = 3m, IsAboveSma5 = true, Atr = 1.5m,
            Week52High = 200m, DistanceFromWeek52HighPercent = -10m,
            RelativeStrengthBenchmarkSymbol = "SPY", RelativeStrengthPercent = 4m,
            Fundamentals = new FundamentalData { Symbol = "AAPL", AsOfDate = Date.AddDays(-30), RevenueGrowthPercent = 8m }
        }
    };

    private static FutureOutcomeLabel Label(int id, int snapshotId, int horizon, string benchmark,
        decimal stockReturn, decimal? benchmarkReturn) => new()
    {
        Id = id, FeatureSnapshotId = snapshotId, HorizonTradingDays = horizon,
        OutcomeDate = Date.AddDays(horizon + 2), BenchmarkSymbol = benchmark,
        ReturnPercent = stockReturn, BenchmarkReturnPercent = benchmarkReturn,
        OutperformedBenchmark = benchmarkReturn.HasValue ? stockReturn > benchmarkReturn.Value : null
    };
}
