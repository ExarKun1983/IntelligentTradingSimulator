using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record BacktestHistoricalMarketDataResult(
    IReadOnlyList<MarketCandle> Candles,
    IReadOnlyDictionary<DateTime, decimal> OpeningPrices,
    IReadOnlySet<DateTime> InvalidCandleDates);

public sealed class BacktestHistoricalMarketDataService
{
    private readonly IMarketDataProvider _marketDataProvider;
    private readonly IMarketCandleRepository? _repository;
    private readonly IWorkflowProgress _progress;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Lazy<Task<BacktestHistoricalMarketDataResult>>> _cachedRanges = new(StringComparer.OrdinalIgnoreCase);

    public BacktestHistoricalMarketDataService(
        IMarketDataProvider marketDataProvider,
        IMarketCandleRepository? repository = null,
        IWorkflowProgress? progress = null,
        TimeProvider? timeProvider = null)
    {
        _marketDataProvider = marketDataProvider;
        _repository = repository;
        _progress = progress ?? NullWorkflowProgress.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyRangeAsync(
        string symbol,
        DateTime from,
        DateTime to)
    {
        return (await GetHistoricalDailyRangeResultAsync(symbol, from, to)).Candles;
    }

    private async Task<BacktestHistoricalMarketDataResult> GetHistoricalDailyRangeResultAsync(
        string symbol,
        DateTime from,
        DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        if (string.IsNullOrWhiteSpace(symbol) || toDate < fromDate)
        {
            return new([], new Dictionary<DateTime, decimal>(), new HashSet<DateTime>());
        }

        symbol = symbol.Trim().ToUpperInvariant();
        var cacheKey = BuildCacheKey(symbol, fromDate, toDate);

        var load =
            _cachedRanges.GetOrAdd(
                cacheKey,
                _ => new Lazy<Task<BacktestHistoricalMarketDataResult>>(
                    () => LoadRangeAsync(symbol, fromDate, toDate)));

        try
        {
            var result = await load.Value;
            if (result.InvalidCandleDates.Count > 0)
            {
                _cachedRanges.TryRemove(
                    new KeyValuePair<string, Lazy<Task<BacktestHistoricalMarketDataResult>>>(cacheKey, load));
            }
            return result;
        }
        catch
        {
            _cachedRanges.TryRemove(
                new KeyValuePair<string, Lazy<Task<BacktestHistoricalMarketDataResult>>>(cacheKey, load));
            throw;
        }
    }

    public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyUpToAsync(
        string symbol,
        DateTime from,
        DateTime to,
        DateTime simulatedDate)
    {
        return (await GetHistoricalDailyUpToWithOpeningPricesAsync(
            symbol, from, to, simulatedDate)).Candles;
    }

    public async Task<BacktestHistoricalMarketDataResult> GetHistoricalDailyUpToWithOpeningPricesAsync(
        string symbol,
        DateTime from,
        DateTime to,
        DateTime simulatedDate)
    {
        var result =
            await GetHistoricalDailyRangeResultAsync(
                symbol,
                from,
                to);

        var simulatedDay = simulatedDate.Date;

        return result with
        {
            Candles = result.Candles.Where(x => x.Date.Date <= simulatedDay).ToList(),
            OpeningPrices = result.OpeningPrices
                .Where(x => x.Key.Date <= simulatedDay)
                .ToDictionary(x => x.Key, x => x.Value),
            InvalidCandleDates = result.InvalidCandleDates
                .Where(x => x.Date <= simulatedDay)
                .ToHashSet()
        };
    }

    private static string BuildCacheKey(
        string symbol,
        DateTime from,
        DateTime to)
    {
        return $"{symbol.ToUpperInvariant()}|{from:yyyyMMdd}|{to:yyyyMMdd}";
    }

    private async Task<BacktestHistoricalMarketDataResult> LoadRangeAsync(
        string symbol,
        DateTime fromDate,
        DateTime toDate)
    {
        // A scoped repository uses one DbContext; serialize distinct concurrent range loads too.
        await _loadLock.WaitAsync();
        try
        {
            var currentDay = _timeProvider.GetUtcNow().UtcDateTime.Date;
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
            var openingPrices = new Dictionary<DateTime, decimal>();
            var invalidCandleDates = new HashSet<DateTime>();
            var upserted = false;
            foreach (var range in missing)
            {
                _progress.Report($"{symbol} | {range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd} | external provider");
                var loaded = await _marketDataProvider.GetHistoricalDailyAsync(symbol, range.From, range.To)
                    ?? throw new InvalidOperationException("The market-data provider returned no response.");
                var bounded = loaded.Where(x => x.Date.Date >= range.From && x.Date.Date <= range.To).ToList();
                ThrowIfConflictingCandles(bounded);
                foreach (var candle in bounded)
                {
                    if (candle.Open > 0m)
                    {
                        openingPrices.TryAdd(candle.Date.Date, candle.Open);
                    }
                    if (_repository is not null && !IsValidCandle(candle))
                    {
                        invalidCandleDates.Add(candle.Date.Date);
                    }
                }

                var validCandles = bounded.Where(IsValidCandle).ToList();
                if (_repository is null)
                {
                    fetched.AddRange(bounded);
                }
                else
                {
                    if (validCandles.Count == bounded.Count)
                    {
                        await _repository.UpsertAsync(symbol, source, mode, range.From, range.To, bounded);
                    }
                    else
                    {
                        foreach (var candle in validCandles.DistinctBy(x => x.Date.Date))
                        {
                            await _repository.UpsertAsync(
                                symbol, source, mode, candle.Date.Date, candle.Date.Date, [candle]);
                        }
                    }
                    upserted = true;
                    fetched.AddRange(validCandles);
                }
            }
            if (_repository is not null && upserted)
                stored = await _repository.GetRangeAsync(symbol, source, mode, fromDate, toDate);
            var origin = missing.Count == 0 ? "database" :
                hasPersistedData ? "database and external provider" : "external provider";
            _progress.Report($"{symbol} | {fromDate:yyyy-MM-dd} to {toDate:yyyy-MM-dd} | {origin}");
            // A competing download may have certified an empty interval first.
            // Only the committed snapshot is authoritative for completed days.
            var nonPersisted = _repository is null ? fetched : fetched.Where(x => x.Date.Date >= currentDay);
            var storedDates = stored.Select(x => x.Date.Date).ToHashSet();
            var candles = stored.Concat(nonPersisted).DistinctBy(x => x.Date.Date)
                .OrderBy(x => x.Date).ToList();
            var openings = stored.ToDictionary(x => x.Date.Date, x => x.Open);
            foreach (var (date, open) in openingPrices)
            {
                if (!storedDates.Contains(date))
                {
                    openings.TryAdd(date, open);
                }
            }

            invalidCandleDates.ExceptWith(storedDates);
            return new(candles, openings, invalidCandleDates);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private static bool IsValidCandle(MarketCandle candle) =>
        candle.Open > 0m && candle.High > 0m && candle.Low > 0m && candle.Close > 0m &&
        candle.Volume >= 0m && candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.Low <= Math.Min(candle.Open, candle.Close) && candle.Low <= candle.High;

    private static void ThrowIfConflictingCandles(IReadOnlyList<MarketCandle> candles)
    {
        if (candles.GroupBy(x => x.Date.Date).Any(group => group.Skip(1).Any(x =>
                x.Open != group.First().Open || x.High != group.First().High ||
                x.Low != group.First().Low || x.Close != group.First().Close ||
                x.Volume != group.First().Volume)))
        {
            throw new ArgumentException("Conflicting candles were returned for the same date.");
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
