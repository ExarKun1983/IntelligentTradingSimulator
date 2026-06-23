using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestHistoricalMarketDataService
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<MarketCandle>>> _cachedRanges = new(StringComparer.OrdinalIgnoreCase);

    public BacktestHistoricalMarketDataService(
        IMarketDataProvider marketDataProvider)
    {
        _marketDataProvider = marketDataProvider;
    }

    public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyRangeAsync(
        string symbol,
        DateTime from,
        DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        if (string.IsNullOrWhiteSpace(symbol) || toDate < fromDate)
        {
            return [];
        }

        var cacheKey = BuildCacheKey(symbol, fromDate, toDate);

        var loadTask =
            _cachedRanges.GetOrAdd(
                cacheKey,
                _ => LoadRangeAsync(symbol, fromDate, toDate));

        return await loadTask;
    }

    public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyUpToAsync(
        string symbol,
        DateTime from,
        DateTime to,
        DateTime simulatedDate)
    {
        var candles =
            await GetHistoricalDailyRangeAsync(
                symbol,
                from,
                to);

        var simulatedDay = simulatedDate.Date;

        return candles
            .Where(x => x.Date.Date <= simulatedDay)
            .ToList();
    }

    private static string BuildCacheKey(
        string symbol,
        DateTime from,
        DateTime to)
    {
        return $"{symbol.ToUpperInvariant()}|{from:yyyyMMdd}|{to:yyyyMMdd}";
    }

    private async Task<IReadOnlyList<MarketCandle>> LoadRangeAsync(
        string symbol,
        DateTime fromDate,
        DateTime toDate)
    {
        var loaded =
            await _marketDataProvider.GetHistoricalDailyAsync(
                symbol,
                fromDate,
                toDate);

        return (loaded ?? [])
            .Where(x =>
                x.Date.Date >= fromDate
                && x.Date.Date <= toDate)
            .OrderBy(x => x.Date)
            .ToList();
    }
}
