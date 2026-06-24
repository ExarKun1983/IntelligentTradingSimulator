using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestBenchmarkServiceTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;
    private readonly FakeBenchmarkProvider _benchmarkProvider;
    private readonly BenchmarkDataService _benchmarkDataService;
    private readonly BacktestBenchmarkService _service;

    public BacktestBenchmarkServiceTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
        _benchmarkProvider = new FakeBenchmarkProvider();
        _benchmarkDataService = new BenchmarkDataService(
            new BacktestHistoricalMarketDataService(_benchmarkProvider));
        _service = new BacktestBenchmarkService(_runRepository, _benchmarkDataService);
    }

    public void Dispose() => _dbContext.Dispose();

    private async Task<BacktestRun> CreateRunAsync(
        DateTime startDate,
        DateTime endDate)
    {
        var run = new BacktestRun
        {
            Name = "Test Run",
            StartDate = startDate,
            EndDate = endDate,
            InitialCapital = 10_000m,
            FinalCapital = 11_000m,
            TotalProfit = 1_000m,
            TotalReturnPercent = 10m,
            Status = IntelligentTradingSimulator.Core.Enums.BacktestRunStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);
        return run;
    }

    [Fact]
    public async Task GetForRunAsync_ReturnsError_WhenRunNotFound()
    {
        var result = await _service.GetForRunAsync(999, BenchmarkUniverse.DefaultSymbols);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task GetForRunAsync_ReturnsRunId_WhenRunExists()
    {
        var run = await CreateRunAsync(
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        _benchmarkProvider.SetData("SPY", CandlesForRange(
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31),
            400m, 420m));

        var result = await _service.GetForRunAsync(run.Id, ["SPY"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(run.Id, result.BacktestRunId);
    }

    [Fact]
    public async Task GetForRunAsync_UsesDatesFromBacktestRun()
    {
        var startDate = new DateTime(2024, 3, 1);
        var endDate = new DateTime(2024, 3, 31);

        var run = await CreateRunAsync(startDate, endDate);

        _benchmarkProvider.SetData("SPY", CandlesForRange(startDate, endDate, 400m, 424m));

        var result = await _service.GetForRunAsync(run.Id, ["SPY"]);

        Assert.True(result.IsSuccess);
        Assert.Single(result.BenchmarkResults);

        var spy = result.BenchmarkResults[0];
        Assert.Equal("SPY", spy.Symbol);
        Assert.Equal(400m, spy.StartPrice);
        Assert.Equal(424m, spy.EndPrice);
        Assert.Equal(6m, spy.ReturnPercent);
    }

    [Fact]
    public async Task GetForRunAsync_ReturnsAllDefaultBenchmarks_WhenDataAvailable()
    {
        var startDate = new DateTime(2024, 1, 1);
        var endDate = new DateTime(2024, 1, 31);
        var run = await CreateRunAsync(startDate, endDate);

        _benchmarkProvider.SetData("SPY", CandlesForRange(startDate, endDate, 400m, 420m));
        _benchmarkProvider.SetData("QQQ", CandlesForRange(startDate, endDate, 300m, 315m));
        _benchmarkProvider.SetData("IWM", CandlesForRange(startDate, endDate, 200m, 190m));

        var result = await _service.GetForRunAsync(
            run.Id,
            BenchmarkUniverse.DefaultSymbols);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.BenchmarkResults.Count);
        Assert.Contains(result.BenchmarkResults, r => r.Symbol == "SPY");
        Assert.Contains(result.BenchmarkResults, r => r.Symbol == "QQQ");
        Assert.Contains(result.BenchmarkResults, r => r.Symbol == "IWM");
    }

    [Fact]
    public async Task GetForRunAsync_ReturnsEmptyBenchmarkResults_WhenNoDataAvailable()
    {
        var run = await CreateRunAsync(
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        var result = await _service.GetForRunAsync(run.Id, BenchmarkUniverse.DefaultSymbols);

        Assert.True(result.IsSuccess);
        Assert.Equal(run.Id, result.BacktestRunId);
        Assert.Empty(result.BenchmarkResults);
    }

    [Fact]
    public async Task GetForRunAsync_SkipsSymbolsWithNoData_WhenPartialDataAvailable()
    {
        var startDate = new DateTime(2024, 1, 1);
        var endDate = new DateTime(2024, 1, 31);
        var run = await CreateRunAsync(startDate, endDate);

        _benchmarkProvider.SetData("SPY", CandlesForRange(startDate, endDate, 400m, 420m));

        var result = await _service.GetForRunAsync(
            run.Id,
            BenchmarkUniverse.DefaultSymbols);

        Assert.True(result.IsSuccess);
        Assert.Single(result.BenchmarkResults);
        Assert.Equal("SPY", result.BenchmarkResults[0].Symbol);
    }

    private static List<MarketCandle> CandlesForRange(
        DateTime from,
        DateTime to,
        decimal startClose,
        decimal endClose)
    {
        return
        [
            new MarketCandle
            {
                Date = from.Date,
                Open = startClose,
                High = startClose,
                Low = startClose,
                Close = startClose,
                Volume = 1_000_000m
            },
            new MarketCandle
            {
                Date = to.Date,
                Open = endClose,
                High = endClose,
                Low = endClose,
                Close = endClose,
                Volume = 1_000_000m
            }
        ];
    }

    private sealed class FakeBenchmarkProvider : IMarketDataProvider
    {
        private readonly Dictionary<string, IReadOnlyList<MarketCandle>> _data
            = new(StringComparer.OrdinalIgnoreCase);

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
