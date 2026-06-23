using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using OoplesFinance.YahooFinanceAPI;
using OoplesFinance.YahooFinanceAPI.Enums;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

public sealed class YahooMarketDataProvider
    : IMarketDataProvider
{
    public async Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
    {
        var yahooClient =
            new YahooClient();

        var data =
            await yahooClient.GetHistoricalDataAsync(
                symbol,
                DataFrequency.Daily,
                from);

        return data
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