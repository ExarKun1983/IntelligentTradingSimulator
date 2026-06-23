using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class BacktestHistoricalMarketDataService
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly Dictionary<string, IReadOnlyList<MarketCandle>> _cachedRanges = new(StringComparer.OrdinalIgnoreCase);

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

        if (_cachedRanges.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var loaded =
            await _marketDataProvider.GetHistoricalDailyAsync(
                symbol,
                fromDate,
                toDate);

        var normalized =
            (loaded ?? [])
            .Where(x =>
                x.Date.Date >= fromDate
                && x.Date.Date <= toDate)
            .OrderBy(x => x.Date)
            .ToList();

        _cachedRanges[cacheKey] = normalized;

        return normalized;
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
}
