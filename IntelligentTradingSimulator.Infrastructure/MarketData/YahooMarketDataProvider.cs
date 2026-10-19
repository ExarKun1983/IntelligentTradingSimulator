using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using OoplesFinance.YahooFinanceAPI;
using OoplesFinance.YahooFinanceAPI.Enums;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

public sealed class YahooMarketDataProvider
    : IMarketDataProvider
{
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

        var yahooClient =
            new YahooClient();

        // Yahoo's period2 is exclusive; the application range is inclusive.
        DateTime? exclusiveEnd = toDate == DateTime.MaxValue.Date
            ? null
            : DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Utc);
        var data =
            await yahooClient.GetHistoricalDataAsync(
                symbol,
                DataFrequency.Daily,
                DateTime.SpecifyKind(fromDate, DateTimeKind.Utc),
                exclusiveEnd);

        return data
            .Where(x =>
                x.Date.Date >= fromDate && x.Date.Date <= toDate)
            .Select(x => new MarketCandle
            {
                Date = x.Date,
                Open = (decimal)x.Open,
                High = (decimal)x.High,
                Low = (decimal)x.Low,
                Close = (decimal)x.Close,
                Volume = (long)x.Volume
            })
            .OrderBy(x => x.Date)
            .ToList();
    }
}