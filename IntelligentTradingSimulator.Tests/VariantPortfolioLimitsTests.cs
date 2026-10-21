using System.Text.Json;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.Tests;

public sealed class VariantPortfolioLimitsTests
{
    private static readonly DateTime Start = new(2024, 1, 5);
    private static readonly DateTime End = Start.AddDays(2);
    private static readonly string[] Symbols = ["AAPL", "MSFT", "GOOG", "AMZN"];
    private const decimal InitialCapital = 100_000m;

    [Fact]
    public async Task Batch_UsesEachVariantsCapsAndRecommendationCounts_InExecutedAllocations()
    {
        using var fixture = new Fixture("uncapped");
        var batch = await fixture.RunBatchAsync(fixture.Variants);

        foreach (var variant in fixture.Variants)
        {
            var result = batch.Results.Single(x => x.StrategyId == variant.Id).EngineResult;
            Assert.Equal(BacktestRunStatus.Completed, result.Status);
            var days = fixture.Days.Where(x => x.RunId == result.RunId).ToArray();
            Assert.Equal(3, days.Length);
            Assert.All(days, day =>
            {
                Assert.Equal(variant.MaxPositions, day.Recommendations.Count);
                Assert.Equal(Symbols.Take(variant.MaxPositions), day.Recommendations.Select(x => x.Symbol));
            });

            var first = days[0];
            Assert.True(first.Recommendations[0].AllocationPercent > 60m);
            Assert.Equal(variant.MaxPositions, first.Trades.Count);
            Assert.Equal(variant.MaxPositions, first.Holdings.Count);
            Assert.All(first.Trades, trade =>
            {
                Assert.Equal(TradeActionType.Buy, trade.Action);
                var recommendation = first.Recommendations.Single(x => x.Symbol == trade.Symbol);
                var target = InitialCapital * Math.Min(recommendation.AllocationPercent, variant.MaxPositionWeight) / 100m;
                // Buy quantities round to 0.0001 shares; half a unit bounds the notional error.
                var tolerance = trade.Price * 0.00005m;
                Assert.InRange(trade.Quantity * trade.Price, target - tolerance, target + tolerance);
                Assert.True(trade.Quantity * trade.Price <= InitialCapital * variant.MaxPositionWeight / 100m + tolerance);
                var holding = first.Holdings.Single(x => x.Symbol == trade.Symbol);
                Assert.Equal(trade.Quantity * trade.Price, holding.Quantity * holding.Price);
            });
            var cappedBuy = first.Trades.Single(x => x.Symbol == "AAPL");
            Assert.Equal(Math.Round(InitialCapital * variant.MaxPositionWeight / 100m / cappedBuy.Price,
                4, MidpointRounding.AwayFromZero), cappedBuy.Quantity);
            var expectedInvestment = first.Recommendations.Sum(x =>
                InitialCapital * Math.Min(x.AllocationPercent, variant.MaxPositionWeight) / 100m);
            Assert.Equal(InitialCapital - expectedInvestment, first.Cash);
            Assert.True(first.Cash > 0m);

            var features = await fixture.Features.QueryAsync(
                DateOnly.FromDateTime(Start), DateOnly.FromDateTime(End), backtestRunId: result.RunId);
            Assert.Equal(Symbols.Length * days.Length, features.Count);
            Assert.All(features, feature =>
            {
                var recorded = JsonSerializer.Deserialize<StrategyConfiguration>(feature.StrategyConfigurationJson)!;
                Assert.Equal(variant.Id, recorded.Id);
                Assert.Equal(variant.MaxPositions, recorded.MaxPositions);
                Assert.Equal(variant.MaxPositionWeight, recorded.MaxPositionWeight);
            });
        }
    }

    [Fact]
    public async Task Batch_IsIndependentOfActiveStrategyAndVariantOrder()
    {
        using var baseline = new Fixture("uncapped");
        var expected = await baseline.RunBatchAsync(baseline.Variants);

        foreach (var active in new[] { "uncapped", "tight" })
        foreach (var reverse in new[] { false, true })
        {
            using var fixture = new Fixture(active);
            var variants = reverse ? fixture.Variants.Reverse().ToArray() : fixture.Variants;
            var batch = await fixture.RunBatchAsync(variants);
            foreach (var variant in variants)
            {
                AssertEquivalent(baseline, expected.Results.Single(x => x.StrategyId == variant.Id).EngineResult,
                    fixture, batch.Results.Single(x => x.StrategyId == variant.Id).EngineResult);
            }
        }
    }

    [Fact]
    public async Task StandaloneAndBatch_ProduceIdenticalRecommendationsTradesDailyValuesAndResults()
    {
        using var batchFixture = new Fixture("uncapped");
        var batch = await batchFixture.RunBatchAsync(batchFixture.Variants);

        foreach (var variant in batchFixture.Variants)
        {
            using var standalone = new Fixture(variant.Id);
            var request = standalone.Services.GetRequiredService<BacktestEngineRequestFactory>().Create(
                "Standalone", Start, End, Start.AddDays(-variant.LookbackDays), InitialCapital, Symbols);
            var result = await standalone.Services.GetRequiredService<IBacktestEngine>().RunAsync(request);

            AssertEquivalent(batchFixture, batch.Results.Single(x => x.StrategyId == variant.Id).EngineResult,
                standalone, result);
        }
    }

    [Theory]
    [InlineData("uncapped", 4, 100)]
    [InlineData("tight", 1, 5)]
    public async Task NormalWorkflows_UseActiveStrategy(string activeId, int maxPositions, int cap)
    {
        using var fixture = new Fixture(activeId);
        var marketData = await fixture.Services.GetRequiredService<MarketDataWorkflow>()
            .CollectAndAnalyzeAsync(Symbols, Start.AddDays(-4));
        Assert.Equal(maxPositions, marketData.Recommendations.Count);
        Assert.True(marketData.Recommendations[0].AllocationPercent > 60m);

        var state = await fixture.Services.GetRequiredService<PortfolioWorkflow>()
            .RunAsync(marketData.Recommendations, marketData.Prices);

        var firstBuy = state.Portfolio.Trades.First();
        var target = 10_000m * 0.80m * Math.Min(marketData.Recommendations[0].AllocationPercent, cap) / 100m;
        Assert.Equal(Math.Round(target / firstBuy.Price, 4, MidpointRounding.AwayFromZero), firstBuy.Quantity);
        var instructions = fixture.Services.GetRequiredService<PortfolioAllocationEngine>().Calculate(
            new Portfolio { StartingCash = InitialCapital, Cash = InitialCapital }, marketData.Recommendations);
        Assert.Equal(InitialCapital * Math.Min(marketData.Recommendations[0].AllocationPercent, cap) / 100m,
            instructions.Single(x => x.Symbol == firstBuy.Symbol).Difference);
        Assert.Equal(maxPositions, state.Portfolio.Positions.Count);
    }

    private static void AssertEquivalent(
        Fixture expectedFixture, BacktestEngineResult expected, Fixture actualFixture, BacktestEngineResult actual)
    {
        Assert.Equal(BacktestRunStatus.Completed, expected.Status);
        Assert.Equal(BacktestRunStatus.Completed, actual.Status);
        var expectedDays = expectedFixture.Days.Where(x => x.RunId == expected.RunId).ToArray();
        var actualDays = actualFixture.Days.Where(x => x.RunId == actual.RunId).ToArray();
        Assert.Equal(3, expectedDays.Length);
        Assert.Equal(expectedDays.Length, actualDays.Length);
        Assert.Equal(JsonSerializer.Serialize(expectedDays.Select(x => x.Recommendations)),
            JsonSerializer.Serialize(actualDays.Select(x => x.Recommendations)));
        Assert.Equal(JsonSerializer.Serialize(expectedDays.Select(x => x.Trades.Select(t =>
                new { t.Timestamp, t.Symbol, t.Action, t.ExitReason, t.Quantity, t.Price }))),
            JsonSerializer.Serialize(actualDays.Select(x => x.Trades.Select(t =>
                new { t.Timestamp, t.Symbol, t.Action, t.ExitReason, t.Quantity, t.Price }))));
        Assert.Equal(JsonSerializer.Serialize(expectedDays.Select(x => new
            {
                x.Snapshot.Date, x.Snapshot.PortfolioValue, x.Snapshot.Cash, x.Snapshot.ProfitLoss,
                x.Snapshot.ReturnPercent, x.Snapshot.TradesExecuted, x.Snapshot.RebalanceExecuted,
                PortfolioCash = x.Cash, x.Holdings
            })),
            JsonSerializer.Serialize(actualDays.Select(x => new
            {
                x.Snapshot.Date, x.Snapshot.PortfolioValue, x.Snapshot.Cash, x.Snapshot.ProfitLoss,
                x.Snapshot.ReturnPercent, x.Snapshot.TradesExecuted, x.Snapshot.RebalanceExecuted,
                PortfolioCash = x.Cash, x.Holdings
            })));
        Assert.Equal((expected.Status, expected.FinalCapital, expected.TotalProfit, expected.TotalReturnPercent, expected.ErrorMessage),
            (actual.Status, actual.FinalCapital, actual.TotalProfit, actual.TotalReturnPercent, actual.ErrorMessage));
    }

    private sealed record Holding(string Symbol, decimal Quantity, decimal Price);
    private sealed record DayCapture(
        int RunId, BacktestSnapshot Snapshot, IReadOnlyList<StockRecommendation> Recommendations,
        List<Trade> Trades, List<Holding> Holdings, decimal Cash);

    private sealed class RecordingDayProcessor(BacktestDayProcessor processor) : IBacktestDayProcessor
    {
        public List<DayCapture> Days { get; } = [];

        public async Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            var tradeCount = request.Portfolio.Trades.Count;
            var result = await processor.ProcessAsync(request);
            Days.Add(new DayCapture(request.BacktestRunId, result.Snapshot, result.Recommendations,
                result.Portfolio.Trades.Skip(tradeCount).ToList(),
                result.Portfolio.Positions.Select(x => new Holding(x.Symbol, x.Quantity, x.CurrentPrice)).ToList(),
                result.Portfolio.Cash));
            return result;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _scope;
        public IServiceProvider Services => _scope.ServiceProvider;
        public StrategyConfiguration[] Variants { get; }
        public List<DayCapture> Days => Services.GetRequiredService<RecordingDayProcessor>().Days;
        public IFeatureSnapshotRepository Features => Services.GetRequiredService<IFeatureSnapshotRepository>();

        public Fixture(string activeId)
        {
            var settings = new Dictionary<string, string?> { ["ActiveStrategy"] = activeId };
            foreach (var (id, count, cap) in new[]
                { ("uncapped", 4, 100), ("tight", 1, 5), ("twenty", 2, 20), ("twenty-five", 3, 25) })
            {
                settings[$"Strategies:{id}:MaxPositions"] = count.ToString();
                settings[$"Strategies:{id}:MaxPositionWeight"] = cap.ToString();
                settings[$"Strategies:{id}:LookbackDays"] = "4";
                settings[$"Strategies:{id}:UseRelativeStrength"] = "false";
                settings[$"Strategies:{id}:MomentumScoreWeight"] = "1";
                settings[$"Strategies:{id}:VolumeTrendScoreWeight"] = "0";
                settings[$"Strategies:{id}:VolatilityScoreWeight"] = "0";
                settings[$"Strategies:{id}:SmaScoreWeight"] = "0";
            }
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var active = StrategyConfigurationLoader.Load(configuration);
            Variants = StrategyConfigurationLoader.LoadAll(configuration)
                .Where(x => x.Id is "twenty" or "twenty-five").OrderBy(x => x.MaxPositionWeight).ToArray();
            var databaseName = Guid.NewGuid().ToString();
            var services = new ServiceCollection();
            services.AddDbContext<TradingSimulatorDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.AddSingleton(active);
            services.AddPortfolioServices(active);
            services.AddSingleton<BacktestEngineRequestFactory>();
            services.AddSingleton<MarketObservationAnalyzerFactory>();
            services.AddSingleton(new MarketObservationAnalyzer(ScoringEngineFactory.Create(active)));
            services.AddSingleton<IMarketDataProvider, DeterministicMarketData>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider());
            services.AddSingleton<ConsoleReporter>();
            services.AddScoped<BacktestHistoricalMarketDataService>();
            services.AddScoped<BacktestDayProcessor>();
            services.AddScoped<RecordingDayProcessor>();
            services.AddScoped<IBacktestDayProcessor>(sp => sp.GetRequiredService<RecordingDayProcessor>());
            services.AddScoped<IBacktestEngine, BacktestEngine>();
            services.AddScoped<IStrategyVariantRunner, StrategyVariantRunner>();
            services.AddScoped<IBacktestRunRepository, BacktestRunRepository>();
            services.AddScoped<IBacktestSnapshotRepository, BacktestSnapshotRepository>();
            services.AddScoped<IFeatureSnapshotRepository, FeatureSnapshotRepository>();
            services.AddScoped<ObservationResultRepository>();
            services.AddScoped<PortfolioRepository>();
            services.AddScoped<PortfolioSnapshotRepository>();
            services.AddScoped<PositionSnapshotRepository>();
            services.AddScoped<MarketDataWorkflow>();
            services.AddScoped<PortfolioWorkflow>();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _provider.CreateScope();
        }

        public Task<StrategyVariantBatchResult> RunBatchAsync(IReadOnlyList<StrategyConfiguration> variants)
            => Services.GetRequiredService<IStrategyVariantRunner>().RunAsync(variants, new StrategyVariantRunRequest
            {
                NamePrefix = "Batch", StartDate = Start, EndDate = End, InitialCapital = InitialCapital, Symbols = Symbols
            });

        public void Dispose()
        {
            _scope.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Start, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class DeterministicMarketData : IMarketDataProvider
    {
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from)
            => GetHistoricalDailyAsync(symbol, from, End);

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to)
        {
            var increment = symbol switch { "AAPL" => 10m, "MSFT" => 1m, "GOOG" => 0.5m, _ => 0.25m };
            IReadOnlyList<MarketCandle> candles = Enumerable.Range(0, 7)
                .Select(day => new MarketCandle
                {
                    Date = Start.AddDays(day - 4),
                    Open = 101.23m + day * increment,
                    High = 102.23m + day * increment,
                    Low = 100.23m + day * increment,
                    Close = 101.23m + day * increment,
                    Volume = 1_000_000m
                }).Where(x => x.Date >= from && x.Date <= to).ToArray();
            return Task.FromResult(candles);
        }
    }
}
