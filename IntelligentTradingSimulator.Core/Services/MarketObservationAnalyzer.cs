using System;
using System.Collections.Generic;
using System.Linq;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class MarketObservationAnalyzer
{
    private readonly StrategyConfiguration _strategyConfiguration;

    public MarketObservationAnalyzer(
        StrategyConfiguration? strategyConfiguration = null)
    {
        _strategyConfiguration =
            strategyConfiguration ?? new StrategyConfiguration();
    }

    public ObservationResult Analyze(
        string symbol,
        IReadOnlyList<MarketCandle> candles)
    {
        var first =
            candles.First();

        var last =
            candles.Last();

        var momentum =
            ((last.Close - first.Close)
             / first.Close) * 100m;

        var averageVolume =
            candles.Average(x => x.Volume);

        var firstVolume =
            candles.First().Volume;

        var lastVolume =
            candles.Last().Volume;

        var volumeTrend =
            ((decimal)lastVolume - firstVolume)
            / firstVolume * 100m;

        var volatility =
            candles.Average(x =>
                (x.High - x.Low)
                / x.Close * 100m);

        var sma5 =
            candles
                .TakeLast(5)
                .Average(x => x.Close);

        var isAboveSma5 =
            last.Close > sma5;



        var score =
            momentum * 10m
            + volumeTrend * 0.05m
            - volatility;

        if (!isAboveSma5)
        {
            score = StrategyConfiguration.DisqualifiedScore;
        }

        if (isAboveSma5)
        {
            score += 5m;
        }

        if (_strategyConfiguration.UseQualityFilter &&
            volatility > StrategyConfiguration.QualityVolatilityThresholdPercent)
        {
            score = StrategyConfiguration.DisqualifiedScore;
        }

        return new ObservationResult
        {
            Symbol = symbol,
            MomentumPercent = momentum,
            AverageVolume = averageVolume,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5,
            Score = score
        };
    }
}