using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Tests;

public sealed class BenchmarkDataServiceTests
{
    private static BenchmarkDataService CreateService(
        FakeBenchmarkMarketDataProvider provider)
    {
        var marketDataService = new BacktestHistoricalMarketDataService(provider);
        return new BenchmarkDataService(marketDataService);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_ReturnsCorrectSymbol()
    {
        var provider = new FakeBenchmarkMarketDataProvider();
        provider.SetData("SPY", Candles(100m, 110m));

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            ["SPY"],
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2));

        Assert.Single(results);
        Assert.Equal("SPY", results[0].Symbol);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_ComputesReturnPercent()
    {
        var provider = new FakeBenchmarkMarketDataProvider();
        provider.SetData("SPY", Candles(100m, 110m));

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            ["SPY"],
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2));

        Assert.Single(results);
        Assert.Equal(100m, results[0].StartPrice);
        Assert.Equal(110m, results[0].EndPrice);
        Assert.Equal(10m, results[0].ReturnPercent);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_ComputesNegativeReturnPercent()
    {
        var provider = new FakeBenchmarkMarketDataProvider();
        provider.SetData("QQQ", Candles(200m, 180m));

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            ["QQQ"],
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2));

        Assert.Single(results);
        Assert.Equal(-10m, results[0].ReturnPercent);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_SkipsSymbolsWithNoData()
    {
        var provider = new FakeBenchmarkMarketDataProvider();
        provider.SetData("SPY", Candles(100m, 105m));

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            ["SPY", "IWM"],
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2));

        Assert.Single(results);
        Assert.Equal("SPY", results[0].Symbol);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_ReturnsEmptyList_WhenNoDataAvailable()
    {
        var provider = new FakeBenchmarkMarketDataProvider();

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            ["SPY", "QQQ", "IWM"],
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 31));

        Assert.Empty(results);
    }

    [Fact]
    public async Task GetBenchmarkResultsAsync_ReturnsResultForAllDefaultBenchmarks()
    {
        var provider = new FakeBenchmarkMarketDataProvider();
        provider.SetData("SPY", Candles(400m, 420m));
        provider.SetData("QQQ", Candles(300m, 315m));
        provider.SetData("IWM", Candles(200m, 190m));

        var service = CreateService(provider);

        var results = await service.GetBenchmarkResultsAsync(
            BenchmarkUniverse.DefaultSymbols,
            new DateTime(2024, 1, 1),
            new DateTime(2024, 1, 2));

        Assert.Equal(3, results.Count);
        Assert.Contains(results, r => r.Symbol == "SPY");
        Assert.Contains(results, r => r.Symbol == "QQQ");
        Assert.Contains(results, r => r.Symbol == "IWM");
    }

    [Fact]
    public void BenchmarkUniverse_DefaultSymbols_ContainsSPY_QQQ_IWM()
    {
        Assert.Contains("SPY", BenchmarkUniverse.DefaultSymbols);
        Assert.Contains("QQQ", BenchmarkUniverse.DefaultSymbols);
        Assert.Contains("IWM", BenchmarkUniverse.DefaultSymbols);
        Assert.Equal(3, BenchmarkUniverse.DefaultSymbols.Length);
    }

    private static List<MarketCandle> Candles(params decimal[] closePrices)
    {
        return closePrices
            .Select((price, index) => new MarketCandle
            {
                Date = new DateTime(2024, 1, 1).AddDays(index),
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = 1_000_000m
            })
            .ToList();
    }

    private sealed class FakeBenchmarkMarketDataProvider : IMarketDataProvider
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
