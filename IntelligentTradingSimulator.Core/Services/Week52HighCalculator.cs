using System;
using System.Collections.Generic;

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
            throw new ArgumentOutOfRangeException(
                nameof(tradingSessions),
                tradingSessions,
                "Trading sessions must be greater than zero.");
        }

        TradingSessions = tradingSessions;
    }

    public int TradingSessions { get; }

    /// <summary>
    /// Calculates the highest high over the configured trailing session
    /// window for chronological market candles. The repository models stock
    /// history as ordered trading sessions, so the default 52-week window is
    /// defined deterministically as 252 trading sessions. The distance formula
    /// is (current close - 52-week high) / 52-week high * 100, which returns 0
    /// at the high and a negative value below it. Values are null when history
    /// is shorter than the configured window, and distance is null when the
    /// high is non-positive.
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

        var startIndex =
            candles.Count - TradingSessions;

        var week52High =
            candles[startIndex].High;

        for (var index = startIndex + 1; index < candles.Count; index++)
        {
            if (candles[index].High > week52High)
            {
                week52High = candles[index].High;
            }
        }

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
