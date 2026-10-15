using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Core.Training;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Training;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public class OfflineModelEvaluationTests
{
    private static readonly DateOnly Date = new(2025, 1, 6);
    private static TrainingDatasetQuery Query => new()
    {
        From = Date, To = Date.AddDays(1), StrategyId = "alpha",
        HorizonTradingDays = 5, BenchmarkSymbol = "SPY"
    };
    private static OfflineEvaluationRequest Request => new()
    {
        ModelId = "fixed-test-model", TrainingCutoff = Date.AddDays(-1),
        OutcomeCutoff = Date.AddDays(10), TopCount = 1
    };

    [Fact]
    public void Evaluate_ComputesModelAndDeterministicBaselineMetrics()
    {
        var report = Evaluate([
            Row("A", -2m, 10m), Row("B", 0m, 0m), Row("C", 2m, -10m)
        ]);

        Assert.Equal(3, report.Model.SampleCount);
        Assert.Equal(100m, report.Model.DirectionalAccuracyPercent);
        Assert.Equal(1d, report.Model.MeanRankCorrelation!.Value, 12);
        Assert.Equal(1, report.Model.RankCorrelationGroupCount);
        Assert.Equal(10m, report.Model.AverageTopForwardReturnPercent);
        Assert.Equal(1, report.Model.TopSelectionCount);
        Assert.Equal(100m / 3m, report.Baseline.DirectionalAccuracyPercent);
        Assert.Equal(-1d, report.Baseline.MeanRankCorrelation!.Value, 12);
        Assert.Equal(-10m, report.Baseline.AverageTopForwardReturnPercent);
        Assert.Equal("Composite", report.BaselineName);
        Assert.Equal(Query, report.Query);
        Assert.Equal(Request, report.Request);
        Assert.Equal(1, report.SchemaVersion);
    }

    [Fact]
    public void Evaluate_UsesAverageRanksForTies_AndOrdinalSymbolForSelections()
    {
        var rows = new[] { Row("B", 1m, 2m), Row("A", 1m, 1m), Row("C", 2m, 3m) };
        var report = Evaluate(rows, request: Request with { TopCount = 2 });
        Assert.Equal(Math.Sqrt(0.75), report.Baseline.MeanRankCorrelation!.Value, 12);
        Assert.Equal(2m, report.Baseline.AverageTopForwardReturnPercent);
        Assert.Equal(new[] { "A", "B", "C" }, report.Rows.Select(row => row.Symbol));
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(Evaluate(rows.Reverse().ToArray(),
            request: Request with { TopCount = 2 })));
    }

    [Fact]
    public void Evaluate_AggregatesSeparateDateAndRunCohorts()
    {
        var report = Evaluate([
            Row("A", 1m, 1m), Row("B", 2m, 3m),
            Row("A", 1m, 10m, date: Date.AddDays(1)),
            Row("B", 2m, 6m, date: Date.AddDays(1)),
            Row("A", 1m, 20m, run: 42)
        ]);
        Assert.Equal(2, report.Baseline.RankCorrelationGroupCount);
        Assert.Equal(0d, report.Baseline.MeanRankCorrelation!.Value, 12);
        Assert.Equal(3, report.Baseline.TopSelectionCount);
        Assert.Equal(29m / 3m, report.Baseline.AverageTopForwardReturnPercent);
    }

    [Fact]
    public void Evaluate_FiltersDateContextHorizonAndImmatureOutcomes_BeforeScoring()
    {
        var calls = 0;
        var report = Evaluate([
            Row("A", 1m, 2m),
            Row("OLD", 1m, 2m, date: Date.AddDays(-1)),
            Row("FUTURE", 1m, 2m, date: Date.AddDays(2)),
            Row("IMMATURE", 1m, 2m, outcome: Request.OutcomeCutoff.AddDays(1)),
            Row("STRATEGY", 1m, 2m, strategy: "other"),
            Row("HORIZON", 1m, 2m, horizon: 20),
            Row("BENCHMARK", 1m, 2m, benchmark: "QQQ")
        ], adapter: new Adapter(context =>
        {
            calls++;
            Assert.Equal("A", context.Symbol);
            Assert.Equal(Date, context.Features!.AnalysisDate);
            return 1m;
        }));
        Assert.Equal(1, calls);
        Assert.Single(report.Rows);
        Assert.Equal(6, report.ExcludedRowCount);
    }

    [Fact]
    public void Evaluate_AppliesSymbolAndRunFilters_AndIncludesCutoffBoundary()
    {
        var dataset = new TrainingDataset
        {
            Query = Query with { Symbol = "A", BacktestRunId = 42 },
            Rows = [
                Row("A", 1m, 2m, run: 42, outcome: Request.OutcomeCutoff),
                Row("B", 1m, 2m, run: 42), Row("A", 1m, 2m)
            ]
        };
        var report = Service().Evaluate(dataset, Request);
        Assert.Single(report.Rows);
        Assert.Equal(Request.OutcomeCutoff, report.Rows[0].OutcomeDate);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("outcome")]
    [InlineData("reversed")]
    [InlineData("top")]
    [InlineData("model")]
    [InlineData("horizon")]
    [InlineData("benchmark")]
    [InlineData("strategy")]
    [InlineData("schema")]
    public void Evaluate_RejectsInvalidRequests(string invalid)
    {
        var request = invalid switch
        {
            "overlap" => Request with { TrainingCutoff = Date },
            "outcome" => Request with { OutcomeCutoff = Date },
            "top" => Request with { TopCount = 0 },
            "model" => Request with { ModelId = " " },
            _ => Request
        };
        var query = invalid switch
        {
            "reversed" => Query with { From = Query.To.AddDays(1) },
            "horizon" => Query with { HorizonTradingDays = null },
            "benchmark" => Query with { BenchmarkSymbol = null },
            "strategy" => Query with { StrategyId = null },
            _ => Query
        };
        var dataset = new TrainingDataset { Query = query, Rows = [], SchemaVersion = invalid == "schema" ? 2 : 1 };
        Assert.Throws<ArgumentException>(() => Service().Evaluate(dataset, request));
    }

    [Theory]
    [InlineData("feature-date")]
    [InlineData("symbol")]
    [InlineData("fundamentals")]
    [InlineData("outcome")]
    [InlineData("schema")]
    [InlineData("duplicate")]
    public void Evaluate_RejectsInvalidRowsBeforeAnyScoring(string invalid)
    {
        var calls = 0;
        var bad = Row("B", 1m, 2m,
            featureDate: invalid == "feature-date" ? Date.AddDays(1) : null,
            featureSymbol: invalid == "symbol" ? "OTHER" : null,
            fundamentalDate: invalid == "fundamentals" ? Date.AddDays(1) : null,
            outcome: invalid == "outcome" ? Date : null,
            schema: invalid == "schema" ? 2 : 1);
        var rows = new[] { Row("A", 1m, 2m), bad }.ToList();
        if (invalid == "duplicate")
            rows.Add(bad);
        Assert.Throws<ArgumentException>(() => Evaluate(rows, adapter: new Adapter(_ => { calls++; return 1m; })));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Evaluate_EmptySingletonAndConstantRanks_ReportUnavailableCorrelation()
    {
        var empty = Evaluate([]);
        Assert.Null(empty.Model.DirectionalAccuracyPercent);
        Assert.Null(empty.Model.MeanRankCorrelation);
        Assert.Null(empty.Model.AverageTopForwardReturnPercent);
        Assert.Equal(0, empty.Model.SampleCount);
        Assert.Null(Evaluate([Row("A", 1m, 2m)]).Model.MeanRankCorrelation);
        Assert.Null(Evaluate([Row("A", 1m, 2m), Row("B", 1m, 3m)]).Baseline.MeanRankCorrelation);
        Assert.Null(Evaluate([Row("A", 1m, 2m), Row("B", 2m, 2m)]).Baseline.MeanRankCorrelation);
    }

    [Fact]
    public void Evaluate_ExcludesDisqualifiedScoresFromEachEnginesMetricsAndSelections()
    {
        var report = Evaluate([
            Row("A", 1m, 2m, aboveSma: false), Row("B", 2m, 3m)
        ], adapter: new Adapter(context => context.Symbol == "B" ? StrategyConfiguration.DisqualifiedScore : 1m));
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(1, report.Model.SampleCount);
        Assert.Equal(2m, report.Model.AverageTopForwardReturnPercent);
        Assert.Equal(1, report.Baseline.SampleCount);
        Assert.Equal(3m, report.Baseline.AverageTopForwardReturnPercent);
    }

    [Fact]
    public void Evaluate_DefaultAiPlaceholderMatchesBaseline_AndPreservesCustomConfiguration()
    {
        var configuration = new StrategyConfiguration { MomentumScoreWeight = 2m, SmaScoreWeight = 0m };
        var service = new OfflineModelEvaluationService(new AiScoringEngine(configuration), configuration);
        var report = service.Evaluate(new TrainingDataset { Query = Query, Rows = [Row("A", 3m, 5m)] }, Request);
        Assert.Equal(report.Baseline, report.Model);
        Assert.Equal(6m, report.Rows[0].BaselineScore);
        configuration.MomentumScoreWeight = 100m;
        Assert.Equal(2m, JsonSerializer.Deserialize<StrategyConfiguration>(report.BaselineConfigurationJson)!.MomentumScoreWeight);
    }

    [Fact]
    public void Evaluate_RepeatableAcrossDateRanges_AndTopCountCanExceedUniverse()
    {
        var rows = new[] { Row("A", 1m, 2m), Row("A", 2m, 4m, date: Date.AddDays(1)) };
        var service = Service();
        var first = service.Evaluate(new TrainingDataset { Query = Query with { To = Date }, Rows = rows },
            Request with { TopCount = 100 });
        var second = service.Evaluate(new TrainingDataset { Query = Query with { From = Date.AddDays(1) }, Rows = rows },
            Request with { TopCount = 100 });
        Assert.Equal(2m, first.Model.AverageTopForwardReturnPercent);
        Assert.Equal(4m, second.Model.AverageTopForwardReturnPercent);
        Assert.Equal(1, first.ExcludedRowCount);
        Assert.Equal(1, second.ExcludedRowCount);
    }

    [Fact]
    public async Task ExportAsync_PreservesReproductionMetadataScoresAndNullMetrics()
    {
        var report = Evaluate([Row("A", 1m, 2m)]);
        using var destination = new MemoryStream();
        await OfflineEvaluationJsonExporter.ExportAsync(report, destination);
        var json = JsonDocument.Parse(destination.ToArray()).RootElement;
        Assert.Equal(1, json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("fixed-test-model", json.GetProperty("request").GetProperty("modelId").GetString());
        Assert.Equal("2025-01-05", json.GetProperty("request").GetProperty("trainingCutoff").GetString());
        Assert.Equal("SPY", json.GetProperty("query").GetProperty("benchmarkSymbol").GetString());
        Assert.Equal("Composite", json.GetProperty("baselineName").GetString());
        Assert.Equal(10m, json.GetProperty("rows")[0].GetProperty("baselineScore").GetDecimal());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("model").GetProperty("meanRankCorrelation").ValueKind);
        destination.Position = 0;
        var restored = await JsonSerializer.DeserializeAsync<OfflineEvaluationReport>(destination,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.Equal(JsonSerializer.Serialize(report), JsonSerializer.Serialize(restored));
    }

    [Fact]
    public async Task ExportAsync_RejectsNullsAndHonorsCancellation()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(() => OfflineEvaluationJsonExporter.ExportAsync(null!, stream));
        await Assert.ThrowsAsync<ArgumentNullException>(() => OfflineEvaluationJsonExporter.ExportAsync(Evaluate([]), null!));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            OfflineEvaluationJsonExporter.ExportAsync(Evaluate([]), stream, cancellation.Token));
    }

    [Fact]
    public async Task Evaluate_StoredHistoricalDataset_CanBeExportedWithoutMarketData()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new TradingSimulatorDbContext(options);
        var source = Row("A", 1m, 2m);
        var snapshot = new FeatureSnapshot
        {
            Symbol = source.Symbol, AnalysisDate = source.AnalysisDate, StrategyId = source.StrategyId,
            Features = source.Features, StrategyConfigurationJson = "{}"
        };
        context.FeatureSnapshots.Add(snapshot);
        await context.SaveChangesAsync();
        context.FutureOutcomeLabels.Add(new FutureOutcomeLabel
        {
            FeatureSnapshotId = snapshot.Id, HorizonTradingDays = source.HorizonTradingDays,
            OutcomeDate = source.OutcomeDate, ReturnPercent = source.ReturnPercent,
            BenchmarkSymbol = source.BenchmarkSymbol
        });
        await context.SaveChangesAsync();
        var builder = new TrainingDatasetBuilder(
            new FeatureSnapshotRepository(context), new FutureOutcomeLabelRepository(context));

        var dataset = await builder.BuildAsync(Query);
        var report = Service().Evaluate(dataset, Request);
        using var output = new MemoryStream();
        await OfflineEvaluationJsonExporter.ExportAsync(report, output);

        Assert.Single(report.Rows);
        Assert.Equal(snapshot.Id, report.Rows[0].FeatureSnapshotId);
        Assert.Equal(2m, report.Model.AverageTopForwardReturnPercent);
        Assert.True(output.Length > 0);
        Assert.Equal(1, await context.FeatureSnapshots.CountAsync());
        Assert.Equal(1, await context.FutureOutcomeLabels.CountAsync());
    }

    [Fact]
    public void Evaluate_RejectsNullInputs_AndPropagatesModelFailures()
    {
        var dataset = new TrainingDataset { Query = Query, Rows = [Row("A", 1m, 2m)] };
        Assert.Throws<ArgumentNullException>(() => Service().Evaluate(null!, Request));
        Assert.Throws<ArgumentNullException>(() => Service().Evaluate(dataset, null!));
        var failure = new InvalidOperationException("Local model failed.");
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() =>
            Service(new Adapter(_ => throw failure)).Evaluate(dataset, Request)));
    }

    private static OfflineModelEvaluationService Service(IScoringEngine? adapter = null) => new(
        new AiScoringEngine(scoringAdapter: adapter ?? new Adapter(context => -context.MomentumPercent)),
        new StrategyConfiguration { SmaScoreWeight = 0m });

    private static OfflineEvaluationReport Evaluate(
        IReadOnlyList<TrainingDatasetRow> rows, OfflineEvaluationRequest? request = null, IScoringEngine? adapter = null) =>
        Service(adapter).Evaluate(new TrainingDataset { Query = Query, Rows = rows }, request ?? Request);

    private static TrainingDatasetRow Row(
        string symbol, decimal momentum, decimal forwardReturn, DateOnly? date = null, DateOnly? outcome = null,
        string strategy = "alpha", int horizon = 5, string benchmark = "SPY", int? run = null,
        DateOnly? featureDate = null, string? featureSymbol = null, DateOnly? fundamentalDate = null,
        int schema = 1, bool aboveSma = true) => new()
    {
        FeatureSnapshotId = symbol[0], FeatureSchemaVersion = schema, FutureOutcomeLabelId = symbol[0],
        Symbol = symbol, AnalysisDate = date ?? Date, StrategyId = strategy, BacktestRunId = run,
        Features = new StockRankingFeatures
        {
            Symbol = featureSymbol ?? symbol, AnalysisDate = featureDate ?? date ?? Date,
            MomentumPercent = momentum, AverageVolume = 1000m, VolumeTrendPercent = 0m,
            VolatilityPercent = 0m, IsAboveSma5 = aboveSma,
            Fundamentals = fundamentalDate.HasValue ? new FundamentalData { Symbol = symbol, AsOfDate = fundamentalDate } : null
        },
        HorizonTradingDays = horizon, OutcomeDate = outcome ?? (date ?? Date).AddDays(7),
        ReturnPercent = forwardReturn, BenchmarkSymbol = benchmark
    };

    private sealed class Adapter(Func<ScoringContext, decimal> score) : IScoringEngine
    {
        public decimal CalculateScore(ScoringContext context) => score(context);
    }
}
