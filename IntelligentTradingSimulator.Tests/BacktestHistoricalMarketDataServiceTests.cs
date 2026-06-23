using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.App.Simulation;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestHistoricalMarketDataServiceTests
{
    [Fact]
    public async Task GetHistoricalDailyRangeAsync_ReturnsCandlesWithinRequestedRange()
    {
        var provider = new FakeMarketDataProvider();
        provider.SetData(
            "AAPL",
            Candles(
                new DateTime(2024, 1, 1),
                new DateTime(2024, 1, 2),
                new DateTime(2024, 1, 3),
                new DateTime(2024, 1, 4),
                new DateTime(2024, 1, 5)));

        var service = new BacktestHistoricalMarketDataService(provider);

        var result =
            await service.GetHistoricalDailyRangeAsync(
                "AAPL",
                new DateTime(2024, 1, 2),
                new DateTime(2024, 1, 4));

        Assert.Equal(3, result.Count);
        Assert.Equal(new DateTime(2024, 1, 2), result[0].Date.Date);
        Assert.Equal(new DateTime(2024, 1, 4), result[^1].Date.Date);
        Assert.Equal(1, provider.RangeCallCount);
        Assert.Equal(new DateTime(2024, 1, 2), provider.LastRangeFrom);
        Assert.Equal(new DateTime(2024, 1, 4), provider.LastRangeTo);
    }

    [Fact]
    public async Task GetHistoricalDailyUpToAsync_UsesCachedRangeAcrossSimulatedDays()
    {
        var provider = new FakeMarketDataProvider();
        provider.SetData(
            "MSFT",
            Candles(
                new DateTime(2024, 2, 1),
                new DateTime(2024, 2, 2),
                new DateTime(2024, 2, 3),
                new DateTime(2024, 2, 4)));

        var service = new BacktestHistoricalMarketDataService(provider);

        _ =
            await service.GetHistoricalDailyUpToAsync(
                "MSFT",
                new DateTime(2024, 2, 1),
                new DateTime(2024, 2, 4),
                new DateTime(2024, 2, 2));

        _ =
            await service.GetHistoricalDailyUpToAsync(
                "MSFT",
                new DateTime(2024, 2, 1),
                new DateTime(2024, 2, 4),
                new DateTime(2024, 2, 3));

        Assert.Equal(1, provider.RangeCallCount);
    }

    [Fact]
    public async Task GetHistoricalDailyUpToAsync_DoesNotIncludeFutureCandlesForSimulatedDay()
    {
        var provider = new FakeMarketDataProvider();
        provider.SetData(
            "TSLA",
            Candles(
                new DateTime(2024, 3, 1),
                new DateTime(2024, 3, 2),
                new DateTime(2024, 3, 3),
                new DateTime(2024, 3, 4)));

        var service = new BacktestHistoricalMarketDataService(provider);

        var result =
            await service.GetHistoricalDailyUpToAsync(
                "TSLA",
                new DateTime(2024, 3, 1),
                new DateTime(2024, 3, 4),
                new DateTime(2024, 3, 2));

        Assert.Equal(2, result.Count);
        Assert.All(
            result,
            candle => Assert.True(candle.Date.Date <= new DateTime(2024, 3, 2)));
        Assert.DoesNotContain(
            result,
            candle => candle.Date.Date > new DateTime(2024, 3, 2));
    }

    [Fact]
    public async Task GetHistoricalDailyUpToAsync_ReturnsEmpty_WhenDataIsMissing()
    {
        var provider = new FakeMarketDataProvider();
        var service = new BacktestHistoricalMarketDataService(provider);

        var result =
            await service.GetHistoricalDailyUpToAsync(
                "UNKNOWN",
                new DateTime(2024, 1, 1),
                new DateTime(2024, 1, 31),
                new DateTime(2024, 1, 15));

        Assert.Empty(result);
    }

    private static List<MarketCandle> Candles(params DateTime[] dates)
    {
        return dates
            .Select(date => new MarketCandle
            {
                Date = date,
                Open = 100m,
                High = 101m,
                Low = 99m,
                Close = 100m,
                Volume = 1_000_000m
            })
            .ToList();
    }

    private sealed class FakeMarketDataProvider
        : IMarketDataProvider
    {
        private readonly Dictionary<string, IReadOnlyList<MarketCandle>> _data = new(StringComparer.OrdinalIgnoreCase);

        public int RangeCallCount { get; private set; }

        public DateTime LastRangeFrom { get; private set; }

        public DateTime LastRangeTo { get; private set; }

        public void SetData(
            string symbol,
            IReadOnlyList<MarketCandle> candles)
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
            RangeCallCount++;
            LastRangeFrom = from.Date;
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
