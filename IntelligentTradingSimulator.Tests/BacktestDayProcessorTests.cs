using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestDayProcessorTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestSnapshotRepository _snapshotRepository;

    public BacktestDayProcessorTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _snapshotRepository = new BacktestSnapshotRepository(_dbContext);
    }

    public void Dispose() => _dbContext.Dispose();

    private BacktestDayProcessor BuildProcessor(
        FakeMarketDataProvider provider,
        int maxPositions = 10)
    {
        var historicalService = new BacktestHistoricalMarketDataService(provider);
        var analyzer = new MarketObservationAnalyzer();
        var recommendationService =
            new PortfolioRecommendationService(maxPositions);
        var allocationService = new PortfolioAllocationService();
        var allocationEngine = new PortfolioAllocationEngine(allocationService);
        var tradingEngine = new TradingEngine();
        var rebalancingService = new PortfolioRebalancingService();
        var performanceService = new PortfolioPerformanceService();

        return new BacktestDayProcessor(
            historicalService,
            analyzer,
            recommendationService,
            rebalancingService,
            allocationEngine,
            tradingEngine,
            performanceService,
            _snapshotRepository);
    }

    private static List<MarketCandle> BuildCandles(
        DateTime startDate,
        int count,
        decimal startPrice = 100m)
    {
        return Enumerable.Range(0, count)
            .Select(i => new MarketCandle
            {
                Date = startDate.AddDays(i),
                Open = startPrice + i,
                High = startPrice + i + 1m,
                Low = startPrice + i - 1m,
                Close = startPrice + i,
                Volume = 1_000_000m
            })
            .ToList();
    }

    [Fact]
    public async Task ProcessAsync_CreatesAndPersistsSnapshot()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 1, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 150m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 300m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 1,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.Snapshot.BacktestRunId);
        Assert.Equal(request.CurrentDate.Date, result.Snapshot.Date);

        var saved = await _snapshotRepository.GetByRunIdAsync(1);
        Assert.Single(saved);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotIncludeFutureData()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 2, 1);
        var simulatedDate = startDate.AddDays(4);

        // 10 candles total; only the first 5 (up to simulatedDate) must be used.
        provider.SetData("TSLA", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 2,
            CurrentDate = simulatedDate,
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["TSLA"]
        };

        var result = await processor.ProcessAsync(request);

        // The provider must have been queried with to=simulatedDate (no look-ahead).
        Assert.Equal(simulatedDate.Date, provider.LastRangeTo);
        Assert.Equal(simulatedDate.Date, result.Snapshot.Date);
    }

    [Fact]
    public async Task ProcessAsync_ToleratesMissingSymbolData()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 3, 1);

        // AAPL has data; MISSING has none.
        provider.SetData("AAPL", BuildCandles(startDate, 10, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 3,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MISSING"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(3, result.Snapshot.BacktestRunId);
    }

    [Fact]
    public async Task ProcessAsync_StillCompletesWhenNoRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 4, 1);

        // Only 3 candles — fewer than the minimum of 5, so no analysis or recommendations.
        provider.SetData("AAPL", BuildCandles(startDate, 3, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 4,
            CurrentDate = startDate.AddDays(2),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Empty(result.Recommendations);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(10_000m, result.Portfolio.Cash);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsUpdatedPortfolioAndSnapshot()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 5, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 150m));

        var processor = BuildProcessor(provider);

        var currentDate = startDate.AddDays(9);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 5,
            CurrentDate = currentDate,
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result.Portfolio);
        Assert.Equal(currentDate.Date, result.CurrentDate);
        Assert.Equal(currentDate.Date, result.Snapshot.Date);
        Assert.Equal(5, result.Snapshot.BacktestRunId);
        Assert.True(result.Snapshot.PortfolioValue > 0m);
    }

    [Fact]
    public async Task ProcessAsync_SetsZeroTradesExecutedAndFalseRebalanceExecuted_WhenNoRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 6, 1);

        // Only 3 candles — fewer than the minimum of 5, so no recommendations or trades.
        provider.SetData("AAPL", BuildCandles(startDate, 3, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 6,
            CurrentDate = startDate.AddDays(2),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Equal(0, result.Snapshot.TradesExecuted);
        Assert.False(result.Snapshot.RebalanceExecuted);
    }

    [Fact]
    public async Task ProcessAsync_SetsRebalanceExecutedTrue_WhenRecommendationsExist()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 7, 1);

        // Enough candles to produce recommendations and trigger rebalancing.
        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 7,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        // Recommendations were available, so rebalance must have been executed.
        Assert.True(result.Snapshot.RebalanceExecuted);
    }

    [Fact]
    public async Task ProcessAsync_TradesExecutedMatchesPortfolioTradesAdded()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 8, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m };

        var request = new BacktestDayRequest
        {
            BacktestRunId = 8,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = portfolio,
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        // The snapshot must record exactly as many trades as were added to the portfolio.
        Assert.Equal(result.Portfolio.Trades.Count, result.Snapshot.TradesExecuted);
    }

    [Fact]
    public async Task ProcessAsync_RespectsMaxPositionsSetting_WhenBuildingRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 9, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 300m));

        var processor = BuildProcessor(provider, maxPositions: 1);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 9,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Single(result.Recommendations);
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly Dictionary<string, IReadOnlyList<MarketCandle>> _data
            = new(StringComparer.OrdinalIgnoreCase);

        public DateTime LastRangeTo { get; private set; }

        public void SetData(string symbol, IReadOnlyList<MarketCandle> candles)
        {
            _data[symbol] = candles;
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            return GetHistoricalDailyAsync(symbol, from, DateTime.MaxValue);
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
        {
            LastRangeTo = to.Date;

            if (!_data.TryGetValue(symbol, out var candles))
            {
                return Task.FromResult<IReadOnlyList<MarketCandle>>([]);
            }

            var result = candles
                .Where(x => x.Date.Date >= from.Date && x.Date.Date <= to.Date)
                .OrderBy(x => x.Date)
                .ToList();

            return Task.FromResult<IReadOnlyList<MarketCandle>>(result);
        }
    }
}
