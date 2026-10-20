using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IMarketDataProvider
{
    // An empty list confirms no candles exist in the requested range.
    // Transient, malformed, null, or unclassified responses must throw instead.
    Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from);

    Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to);
}