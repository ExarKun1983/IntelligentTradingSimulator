using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestHistoricalMarketDataService
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IMarketCandleRepository? _repository;
    private readonly IWorkflowProgress _progress;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<MarketCandle>>>> _cachedRanges = new(StringComparer.OrdinalIgnoreCase);

    public BacktestHistoricalMarketDataService(
        IMarketDataProvider marketDataProvider,
        IMarketCandleRepository? repository = null,
        IWorkflowProgress? progress = null)
    {
        _marketDataProvider = marketDataProvider;
        _repository = repository;
        _progress = progress ?? NullWorkflowProgress.Instance;
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

        symbol = symbol.Trim().ToUpperInvariant();
        var cacheKey = BuildCacheKey(symbol, fromDate, toDate);

        var load =
            _cachedRanges.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<IReadOnlyList<MarketCandle>>>(() => LoadRangeAsync(symbol, fromDate, toDate)));

        return await load.Value;
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
        // A scoped repository uses one DbContext; serialize distinct concurrent range loads too.
        await _loadLock.WaitAsync();
        try
        {
            var currentDay = DateTime.UtcNow.Date;
            var source = HistoricalMarketDataPolicy.Source;
            var mode = HistoricalMarketDataPolicy.PriceAdjustmentMode;
            var coverage = _repository is null ? [] :
                await _repository.GetCoverageAsync(symbol, source, mode, fromDate, toDate);
            var stored = _repository is null ? [] :
                await _repository.GetRangeAsync(symbol, source, mode, fromDate, toDate);
            var hasPersistedData = stored.Count > 0 || coverage.Count > 0;
            var covered = coverage.Select(x => (From: x.From, To: x.To))
                .Concat(stored.Select(x => (From: x.Date.Date, To: x.Date.Date)));
            var missing = FindMissingRanges(fromDate, toDate, covered);
            var fetched = new List<MarketCandle>();
            var upserted = false;
            foreach (var range in missing)
            {
                _progress.Report($"{symbol} | {range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd} | external provider");
                var loaded = await _marketDataProvider.GetHistoricalDailyAsync(symbol, range.From, range.To)
                    ?? throw new InvalidOperationException("The market-data provider returned no response.");
                var bounded = loaded.Where(x => x.Date.Date >= range.From && x.Date.Date <= range.To).ToList();
                if (_repository is not null)
                {
                    await _repository.UpsertAsync(symbol, source, mode, range.From, range.To, bounded);
                    upserted = true;
                }
                fetched.AddRange(bounded);
            }
            if (_repository is not null && upserted)
                stored = await _repository.GetRangeAsync(symbol, source, mode, fromDate, toDate);
            var origin = missing.Count == 0 ? "database" :
                hasPersistedData ? "database and external provider" : "external provider";
            _progress.Report($"{symbol} | {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd} | {origin}");
            // A competing download may have certified an empty interval first.
            // Only the committed snapshot is authoritative for completed days.
            var nonPersisted = _repository is null ? fetched : fetched.Where(x => x.Date.Date >= currentDay);
            return stored.Concat(nonPersisted).DistinctBy(x => x.Date.Date).OrderBy(x => x.Date).ToList();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static List<(DateTime From, DateTime To)> FindMissingRanges(
        DateTime from, DateTime to, IEnumerable<(DateTime From, DateTime To)> covered)
    {
        var missing = new List<(DateTime From, DateTime To)>();
        var next = from;
        foreach (var range in covered.OrderBy(x => x.From))
        {
            if (range.To < next || range.From > to)
                continue;
            if (range.From > next)
                missing.Add((next, range.From.AddDays(-1)));
            if (range.To >= to)
                return missing;
            next = range.To.AddDays(1);
        }
        if (next <= to)
            missing.Add((next, to));
        return missing;
    }
}
