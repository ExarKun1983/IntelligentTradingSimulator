using System;
using System.Collections.Generic;
using System.Linq;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class Week52HighCalculator
{
    public const int DefaultTradingSessions = 252;

    public Week52HighCalculator(
        int tradingSessions = DefaultTradingSessions)
    {
        if (tradingSessions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tradingSessions));
        }

        TradingSessions = tradingSessions;
    }

    public int TradingSessions { get; }

    /// <summary>
    /// Calculates the highest high over the trailing 52-week window for
    /// chronological market candles. The repository models stock history as
    /// ordered trading sessions, so 52 weeks is defined deterministically as
    /// 252 trading sessions. The distance formula is
    /// (current close - 52-week high) / 52-week high * 100, which returns 0 at
    /// the high and a negative value below it.
    /// </summary>
    public Week52HighResult Calculate(
        IReadOnlyList<MarketCandle> candles)
    {
        if (candles is null)
        {
            throw new ArgumentNullException(nameof(candles));
        }

        if (candles.Count < TradingSessions)
        {
            return new Week52HighResult();
        }

        var week52High =
            candles
                .Skip(candles.Count - TradingSessions)
                .Max(x => x.High);

        if (week52High <= 0m)
        {
            return new Week52HighResult
            {
                Week52High = week52High
            };
        }

        var currentPrice =
            candles[candles.Count - 1].Close;

        return new Week52HighResult
        {
            Week52High = week52High,
            DistanceFromWeek52HighPercent =
                (currentPrice - week52High)
                / week52High * 100m
        };
    }
}
