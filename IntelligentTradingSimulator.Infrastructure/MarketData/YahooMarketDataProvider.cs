using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using OoplesFinance.YahooFinanceAPI;
using OoplesFinance.YahooFinanceAPI.Enums;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

public sealed class YahooMarketDataProvider
    : IMarketDataProvider
{
    private readonly Func<string, DateTime, DateTime?, Task<IReadOnlyList<MarketCandle>>> _download;

    public YahooMarketDataProvider()
        : this(DownloadFromYahooAsync)
    {
    }

    internal YahooMarketDataProvider(
        Func<string, DateTime, DateTime?, Task<IReadOnlyList<MarketCandle>>> download)
    {
        _download = download;
    }

    public Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
    {
        return GetHistoricalDailyAsync(
            symbol,
            from,
            DateTime.MaxValue.Date);
    }

    public async Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        if (toDate < fromDate)
        {
            return [];
        }

        // Yahoo's period2 is exclusive; the application range is inclusive.
        DateTime? exclusiveEnd = toDate == DateTime.MaxValue.Date
            ? null
            : DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Utc);
        var data = await _download(
            symbol,
            DateTime.SpecifyKind(fromDate, DateTimeKind.Utc),
            exclusiveEnd);

        return data
            .Where(x =>
                x.Date.Date >= fromDate && x.Date.Date <= toDate)
            .OrderBy(x => x.Date)
            .ToList();
    }

    private static async Task<IReadOnlyList<MarketCandle>> DownloadFromYahooAsync(
        string symbol, DateTime from, DateTime? to)
    {
        var yahooClient = new YahooClient();
        var data = await yahooClient.GetHistoricalDataAsync(symbol, DataFrequency.Daily, from, to);
        return data.Select(x => new MarketCandle
        {
            Date = x.Date,
            Open = (decimal)x.Open,
            High = (decimal)x.High,
            Low = (decimal)x.Low,
            Close = (decimal)x.Close,
            Volume = (long)x.Volume
        }).ToList();
    }
}