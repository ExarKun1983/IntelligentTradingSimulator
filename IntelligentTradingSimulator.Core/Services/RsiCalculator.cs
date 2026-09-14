using System;
using System.Collections.Generic;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class RsiCalculator
{
    public const int DefaultPeriod = 14;

    public RsiCalculator(int period = DefaultPeriod)
    {
        if (period < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        Period = period;
    }

    public int Period { get; }

    /// <summary>
    /// Calculates RSI using Wilder smoothing for chronological closing prices.
    /// Returns <see langword="null"/> when fewer than <c>Period + 1</c> prices are available.
    /// A sequence with no price changes returns the neutral value of 50.
    /// </summary>
    public decimal? Calculate(IReadOnlyList<decimal> closingPrices)
    {
        if (closingPrices is null)
        {
            throw new ArgumentNullException(nameof(closingPrices));
        }

        if (closingPrices.Count <= Period)
        {
            return null;
        }

        decimal averageGain = 0m;
        decimal averageLoss = 0m;

        for (var index = 1; index <= Period; index++)
        {
            AddChange(
                closingPrices[index] - closingPrices[index - 1],
                ref averageGain,
                ref averageLoss);
        }

        averageGain /= Period;
        averageLoss /= Period;

        for (var index = Period + 1; index < closingPrices.Count; index++)
        {
            decimal gain = 0m;
            decimal loss = 0m;
            AddChange(
                closingPrices[index] - closingPrices[index - 1],
                ref gain,
                ref loss);

            averageGain = ((averageGain * (Period - 1)) + gain) / Period;
            averageLoss = ((averageLoss * (Period - 1)) + loss) / Period;
        }

        if (averageLoss == 0m)
        {
            return averageGain == 0m ? 50m : 100m;
        }

        if (averageGain == 0m)
        {
            return 0m;
        }

        var relativeStrength = averageGain / averageLoss;
        return 100m - (100m / (1m + relativeStrength));
    }

    private static void AddChange(
        decimal change,
        ref decimal gain,
        ref decimal loss)
    {
        if (change > 0m)
        {
            gain += change;
        }
        else
        {
            loss -= change;
        }
    }
}
