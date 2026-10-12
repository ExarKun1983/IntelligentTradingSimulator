using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class AtrCalculator
{
    public const int DefaultPeriod = 14;

    public AtrCalculator(int period = DefaultPeriod)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        Period = period;
    }

    public int Period { get; }

    /// <summary>
    /// Calculates ATR in price units using Wilder smoothing for chronological candles.
    /// Returns <see langword="null"/> for fewer than <c>Period + 1</c> candles,
    /// invalid high/low/close prices, or arithmetic overflow.
    /// </summary>
    public decimal? Calculate(IReadOnlyList<MarketCandle> candles)
    {
        ArgumentNullException.ThrowIfNull(candles);

        if (candles.Count <= Period)
        {
            return null;
        }

        foreach (var candle in candles)
        {
            if (candle.Low <= 0m || candle.High < candle.Low ||
                candle.Close < candle.Low || candle.Close > candle.High)
            {
                return null;
            }
        }

        try
        {
            decimal atr = 0m;
            for (var index = 1; index <= Period; index++)
            {
                atr += TrueRange(candles[index], candles[index - 1].Close);
            }

            atr /= Period;

            for (var index = Period + 1; index < candles.Count; index++)
            {
                atr = ((atr * (Period - 1)) +
                    TrueRange(candles[index], candles[index - 1].Close)) / Period;
            }

            return atr;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static decimal TrueRange(MarketCandle candle, decimal previousClose) =>
        Math.Max(candle.High - candle.Low,
            Math.Max(Math.Abs(candle.High - previousClose),
                Math.Abs(candle.Low - previousClose)));
}
