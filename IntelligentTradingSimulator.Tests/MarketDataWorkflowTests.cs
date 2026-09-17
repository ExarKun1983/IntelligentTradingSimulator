using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class MarketDataWorkflowTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;

    public MarketDataWorkflowTests()
    {
        var options =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        _dbContext = new TradingSimulatorDbContext(options);
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task CollectAndAnalyzeAsync_ReturnsNullWeek52Values_WhenProviderHasFewerThan252Sessions()
    {
        var today = DateTime.Today;
        var provider = new FakeMarketDataProvider(
            BuildCandles(today.AddDays(-250), 251));
        var workflow = BuildWorkflow(provider);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["TEST"],
                today.AddDays(-89));

        var observation = Assert.Single(result.RankedResults);
        Assert.Null(observation.Week52High);
        Assert.Null(observation.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_ExposesWeek52Values_WithoutChangingLookbackScoring()
    {
        var today = DateTime.Today;
        var candles = BuildCandles(today.AddDays(-251), 252);
        candles[0].High = 1_000m;
        var provider = new FakeMarketDataProvider(candles);
        var strategy = new StrategyConfiguration
        {
            LookbackDays = 89,
            UseRsi = true
        };
        var workflow = BuildWorkflow(provider, strategy);
        var requestedStartDate = today.AddDays(-89);
        var strategyCandles =
            candles
                .Where(x => x.Date.Date >= requestedStartDate)
                .ToList();
        var expected =
            new MarketObservationAnalyzer(strategy)
                .Analyze("TEST", strategyCandles);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["TEST"],
                requestedStartDate);

        var observation = Assert.Single(result.RankedResults);
        Assert.Equal(1_000m, observation.Week52High);
        Assert.Equal(
            (observation.Week52High.HasValue
                ? (candles[^1].Close - observation.Week52High.Value)
                    / observation.Week52High.Value * 100m
                : null),
            observation.DistanceFromWeek52HighPercent);
        Assert.Equal(expected.Rsi, observation.Rsi);
        Assert.Equal(expected.MomentumPercent, observation.MomentumPercent);
        Assert.Equal(expected.Score, observation.Score);
        Assert.Equal(
            MarketHistoryRequirements.GetEffectiveStartDate(
                requestedStartDate,
                today),
            provider.LastFrom);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_RanksByWeek52HighAdjustedScore_WhenEnabled()
    {
        var today = DateTime.Today;
        var nearHighCandles = BuildCandles(today.AddDays(-251), 252);
        var farBelowHighCandles = BuildCandles(today.AddDays(-251), 252);
        farBelowHighCandles[0].High = 1_000m;

        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["FAR"] = farBelowHighCandles,
                ["NEAR"] = nearHighCandles
            });
        var strategy = new StrategyConfiguration
        {
            LookbackDays = 89,
            Use52WeekHigh = true,
            Week52HighScoreWeight = 10m
        };
        var workflow = BuildWorkflow(provider, strategy);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["FAR", "NEAR"],
                today.AddDays(-89));

        Assert.Collection(
            result.RankedResults,
            first => Assert.Equal("NEAR", first.Symbol),
            second => Assert.Equal("FAR", second.Symbol));
        Assert.True(
            result.RankedResults[0].Score >
            result.RankedResults[1].Score);
    }

    private MarketDataWorkflow BuildWorkflow(
        IMarketDataProvider provider,
        StrategyConfiguration? strategy = null)
    {
        return new MarketDataWorkflow(
            provider,
            new MarketObservationAnalyzer(strategy),
            new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext));
    }

    private static List<MarketCandle> BuildCandles(
        DateTime startDate,
        int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new MarketCandle
            {
                Date = startDate.AddDays(index),
                Open = 100m + index,
                High = 101m + index,
                Low = 99m + index,
                Close = 100m + index,
                Volume = 1_000_000m + index
            })
            .ToList();
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<MarketCandle>>
            _candlesBySymbol;

        public FakeMarketDataProvider(IReadOnlyList<MarketCandle> candles)
            : this(
                new Dictionary<string, IReadOnlyList<MarketCandle>>
                {
                    ["TEST"] = candles
                })
        {
        }

        public FakeMarketDataProvider(
            IReadOnlyDictionary<string, IReadOnlyList<MarketCandle>>
                candlesBySymbol) =>
            _candlesBySymbol = candlesBySymbol;

        public DateTime LastFrom { get; private set; }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            LastFrom = from.Date;

            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                _candlesBySymbol[symbol]
                    .Where(x => x.Date.Date >= from.Date)
                    .OrderBy(x => x.Date)
                    .ToList());
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
        {
            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                _candlesBySymbol[symbol]
                    .Where(x =>
                        x.Date.Date >= from.Date
                        && x.Date.Date <= to.Date)
                    .OrderBy(x => x.Date)
                    .ToList());
        }
    }
}
