using System;
using System.Collections.Generic;
using System.Linq;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class RelativeStrengthCalculator
{
    /// <summary>
    /// Returns stock return minus benchmark return, in percentage points.
    /// Uses the stock's first and last sessions within the inclusive window;
    /// both benchmark endpoints must exist on exactly those dates.
    /// Missing, ambiguous, insufficient, or invalid endpoint prices return null.
    /// </summary>
    public decimal? Calculate(
        IReadOnlyList<MarketCandle>? stockCandles,
        IReadOnlyList<MarketCandle>? benchmarkCandles,
        DateTime from,
        DateTime analysisDate)
    {
        if (stockCandles is null || benchmarkCandles is null)
        {
            return null;
        }

        var stock = stockCandles
            .Where(x => x.Date.Date >= from.Date && x.Date.Date <= analysisDate.Date)
            .OrderBy(x => x.Date)
            .ToList();

        if (stock.Count < 2 || stock[0].Date.Date == stock[^1].Date.Date)
        {
            return null;
        }

        var startDate = stock[0].Date.Date;
        var endDate = stock[^1].Date.Date;
        var stockStart = stock.Where(x => x.Date.Date == startDate).ToList();
        var stockEnd = stock.Where(x => x.Date.Date == endDate).ToList();
        var benchmarkStart = benchmarkCandles.Where(x => x.Date.Date == startDate).ToList();
        var benchmarkEnd = benchmarkCandles.Where(x => x.Date.Date == endDate).ToList();

        if (stockStart.Count != 1 || stockEnd.Count != 1
            || benchmarkStart.Count != 1 || benchmarkEnd.Count != 1
            || stockStart[0].Close <= 0m || stockEnd[0].Close <= 0m
            || benchmarkStart[0].Close <= 0m || benchmarkEnd[0].Close <= 0m)
        {
            return null;
        }

        try
        {
            var stockReturn =
                (stockEnd[0].Close - stockStart[0].Close) / stockStart[0].Close * 100m;
            var benchmarkReturn =
                (benchmarkEnd[0].Close - benchmarkStart[0].Close) / benchmarkStart[0].Close * 100m;
            return stockReturn - benchmarkReturn;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
