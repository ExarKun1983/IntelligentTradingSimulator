using System.Data;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class MarketCandleRepository(TradingSimulatorDbContext dbContext) : IMarketCandleRepository
{
    public async Task<IReadOnlyList<MarketCandle>> GetRangeAsync(
        string symbol, string source, string priceAdjustmentMode, DateTime from, DateTime to)
    {
        symbol = NormalizeSymbol(symbol);
        from = from.Date;
        to = to.Date;
        var rows = await dbContext.MarketCandles.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Source == source && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date).ToListAsync();
        if (rows.Any(x => x.PriceAdjustmentMode != priceAdjustmentMode))
            throw new InvalidOperationException("Cannot mix price-adjustment conventions for a market-data source.");

        return rows.Select(x => new MarketCandle
        {
            Date = x.Date, Open = x.Open, High = x.High, Low = x.Low,
            Close = x.Close, Volume = x.Volume
        }).ToList();
    }

    public async Task<IReadOnlyList<MarketDataCoverage>> GetCoverageAsync(
        string symbol, string source, string priceAdjustmentMode, DateTime from, DateTime to)
    {
        symbol = NormalizeSymbol(symbol);
        from = from.Date;
        to = to.Date;
        return await dbContext.MarketDataCoverage.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Source == source &&
                x.PriceAdjustmentMode == priceAdjustmentMode && x.From <= to && x.To >= from)
            .OrderBy(x => x.From).ToListAsync();
    }

    public async Task UpsertAsync(
        string symbol, string source, string priceAdjustmentMode,
        DateTime from, DateTime to, IReadOnlyList<MarketCandle> candles)
    {
        symbol = NormalizeSymbol(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceAdjustmentMode);
        if (source.Length > 50 || priceAdjustmentMode.Length > 50)
            throw new ArgumentException("Market-data source and price-adjustment mode must not exceed 50 characters.");
        from = from.Date;
        to = to.Date;
        if (to < from)
            throw new ArgumentException("Market-data range end must not precede its start.");

        foreach (var candle in candles)
        {
            if (candle.Date.Date < from || candle.Date.Date > to ||
                candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0 ||
                candle.Volume < 0 || candle.High < Math.Max(candle.Open, candle.Close) ||
                candle.Low > Math.Min(candle.Open, candle.Close) || candle.Low > candle.High)
                throw new ArgumentException("Market candles must contain valid OHLCV values within the fetched range.");
        }
        if (candles.GroupBy(x => x.Date.Date).Any(group => group.Skip(1).Any(x =>
                x.Open != group.First().Open || x.High != group.First().High ||
                x.Low != group.First().Low || x.Close != group.First().Close ||
                x.Volume != group.First().Volume)))
            throw new ArgumentException("Conflicting candles were returned for the same date.");

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable)
            : null;
        var existing = await dbContext.MarketCandles
            .Where(x => x.Symbol == symbol && x.Source == source && x.Date >= from && x.Date <= to)
            .ToListAsync();
        if (existing.Any(x => x.PriceAdjustmentMode != priceAdjustmentMode))
            throw new InvalidOperationException("Cannot mix price-adjustment conventions for a market-data source.");
        var existingDates = existing.Select(x => x.Date).ToHashSet();
        var downloadedAt = DateTime.UtcNow;
        var coverageEnd = to < downloadedAt.Date ? to : downloadedAt.Date.AddDays(-1);

        // First download wins: an upsert must not revise an existing backtest snapshot.
        dbContext.MarketCandles.AddRange(candles.DistinctBy(x => x.Date.Date)
            .Where(x => x.Date.Date <= coverageEnd && !existingDates.Contains(x.Date.Date))
            .Select(x => new MarketCandleEntity
            {
                Symbol = symbol, Source = source, PriceAdjustmentMode = priceAdjustmentMode,
                Date = x.Date.Date, Open = x.Open, High = x.High, Low = x.Low,
                Close = x.Close, Volume = x.Volume, DownloadedAt = downloadedAt
            }));

        // Do not certify today's incomplete session or future dates as fully downloaded.
        if (coverageEnd >= from && !await dbContext.MarketDataCoverage.AnyAsync(x =>
                x.Symbol == symbol && x.Source == source && x.PriceAdjustmentMode == priceAdjustmentMode &&
                x.From <= from && x.To >= coverageEnd))
        {
            dbContext.MarketDataCoverage.Add(new MarketDataCoverage
            {
                Symbol = symbol, Source = source, PriceAdjustmentMode = priceAdjustmentMode,
                From = from, To = coverageEnd, DownloadedAt = downloadedAt
            });
        }
        await dbContext.SaveChangesAsync();
        if (transaction is not null)
            await transaction.CommitAsync();
    }

    private static string NormalizeSymbol(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        symbol = symbol.Trim().ToUpperInvariant();
        if (symbol.Length > 20)
            throw new ArgumentException("Market-data symbols must not exceed 20 characters.", nameof(symbol));
        return symbol;
    }
}
