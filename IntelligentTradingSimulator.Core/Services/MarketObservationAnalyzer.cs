using System;
using System.Collections.Generic;
using System.Linq;

using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class MarketObservationAnalyzer
{
    private readonly IScoringEngine _scoringEngine;
    private readonly RsiCalculator _rsiCalculator;

    public MarketObservationAnalyzer(
        StrategyConfiguration? strategyConfiguration = null)
        : this(
            new RuleBasedScoringEngine(strategyConfiguration),
            new RsiCalculator())
    {
    }

    public MarketObservationAnalyzer(
        IScoringEngine scoringEngine,
        RsiCalculator? rsiCalculator = null)
    {
        _scoringEngine =
            scoringEngine
            ?? throw new ArgumentNullException(nameof(scoringEngine));
        _rsiCalculator = rsiCalculator ?? new RsiCalculator();
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

        var rsi =
            _rsiCalculator.Calculate(
                candles.Select(x => x.Close).ToList());

        var score =
            _scoringEngine.CalculateScore(
                new ScoringContext
                {
                    Symbol = symbol,
                    MomentumPercent = momentum,
                    AverageVolume = averageVolume,
                    VolumeTrendPercent = volumeTrend,
                    VolatilityPercent = volatility,
                    IsAboveSma5 = isAboveSma5
                });

        return new ObservationResult
        {
            Symbol = symbol,
            MomentumPercent = momentum,
            AverageVolume = averageVolume,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5,
            Rsi = rsi,
            Score = score
        };
    }
}