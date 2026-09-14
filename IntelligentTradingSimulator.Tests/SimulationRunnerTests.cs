using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class SimulationRunnerTests : IDisposable
{
    private const int LookbackDays = 90;

    private readonly TradingSimulatorDbContext _dbContext;

    public SimulationRunnerTests()
    {
        var options =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        _dbContext = new TradingSimulatorDbContext(options);
    }

    public void Dispose() => _dbContext.Dispose();

    [Fact]
    public async Task RunAsync_AnalyzesConfiguredStrategyLookback_NotFixedFourteenDays()
    {
        var today = DateTime.Today;
        var candles = BuildCandles(today.AddDays(-399), 400);
        var provider = new FakeMarketDataProvider(candles);
        var strategy = new StrategyConfiguration { LookbackDays = LookbackDays };
        var runner = BuildRunner(provider, strategy);

        var expected =
            Analyze(candles, strategy, today.AddDays(-LookbackDays));

        var fourteenDayExpected =
            Analyze(candles, strategy, today.AddDays(-14));

        await runner.RunAsync();

        var run = await _dbContext.ObservationRuns
            .Include(x => x.Results)
            .SingleAsync();

        var observation = run.Results[0];

        Assert.Equal(expected.MomentumPercent, observation.MomentumPercent);
        Assert.Equal(expected.Score, observation.Score);

        Assert.NotEqual(
            fourteenDayExpected.MomentumPercent,
            observation.MomentumPercent);
    }

    [Fact]
    public async Task RunAsync_RequestsExtendedHistory_SeparateFromStrategyLookback()
    {
        var today = DateTime.Today;
        var candles = BuildCandles(today.AddDays(-399), 400);
        var provider = new FakeMarketDataProvider(candles);
        var strategy = new StrategyConfiguration { LookbackDays = LookbackDays };
        var runner = BuildRunner(provider, strategy);

        var strategyStartDate = today.AddDays(-LookbackDays);

        await runner.RunAsync();

        Assert.Equal(
            MarketHistoryRequirements.GetEffectiveStartDate(
                strategyStartDate,
                today),
            provider.LastFrom);

        Assert.True(provider.LastFrom < strategyStartDate);
    }

    private static ObservationResult Analyze(
        IReadOnlyList<MarketCandle> candles,
        StrategyConfiguration strategy,
        DateTime from)
    {
        var usableCandles =
            candles
                .Where(x => x.Date.Date >= from.Date)
                .ToList();

        return new MarketObservationAnalyzer(strategy)
            .Analyze(
                StockUniverse.Symbols[0],
                usableCandles,
                candles);
    }

    private SimulationRunner BuildRunner(
        IMarketDataProvider provider,
        StrategyConfiguration strategy)
    {
        var allocationService = new PortfolioAllocationService();

        var reporter =
            new ConsoleReporter(
                allocationService,
                new PortfolioContributionService());

        var marketDataWorkflow =
            new MarketDataWorkflow(
                provider,
                new MarketObservationAnalyzer(strategy),
                new PortfolioRecommendationService(strategy.MaxPositions),
                new ObservationResultRepository(_dbContext));

        var portfolioWorkflow =
            new PortfolioWorkflow(
                new PortfolioRepository(_dbContext),
                new PortfolioSnapshotRepository(_dbContext),
                new PositionSnapshotRepository(_dbContext),
                new PortfolioExecutionService(strategy.MaxPositionWeight),
                new TradingEngine(),
                new PortfolioAllocationEngine(
                    allocationService,
                    strategy.MaxPositionWeight),
                new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                reporter);

        return new SimulationRunner(
            marketDataWorkflow,
            portfolioWorkflow,
            reporter,
            strategy);
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

    private sealed class FakeMarketDataProvider(
        IReadOnlyList<MarketCandle> candles)
        : IMarketDataProvider
    {
        public DateTime LastFrom { get; private set; }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            LastFrom = from.Date;

            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                candles
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
                candles
                    .Where(x =>
                        x.Date.Date >= from.Date
                        && x.Date.Date <= to.Date)
                    .OrderBy(x => x.Date)
                    .ToList());
        }
    }
}
