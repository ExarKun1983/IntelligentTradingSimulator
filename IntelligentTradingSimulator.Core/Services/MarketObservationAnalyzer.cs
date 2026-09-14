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
    private readonly Week52HighCalculator _week52HighCalculator;

    public MarketObservationAnalyzer(
        StrategyConfiguration? strategyConfiguration = null)
        : this(
            new RuleBasedScoringEngine(strategyConfiguration),
            new RsiCalculator(),
            new Week52HighCalculator())
    {
    }

    public MarketObservationAnalyzer(
        IScoringEngine scoringEngine,
        RsiCalculator? rsiCalculator = null,
        Week52HighCalculator? week52HighCalculator = null)
    {
        _scoringEngine =
            scoringEngine
            ?? throw new ArgumentNullException(nameof(scoringEngine));
        _rsiCalculator = rsiCalculator ?? new RsiCalculator();
        _week52HighCalculator =
            week52HighCalculator ?? new Week52HighCalculator();
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

        var week52High =
            _week52HighCalculator.Calculate(candles);

        var score =
            _scoringEngine.CalculateScore(
                new ScoringContext
                {
                    Symbol = symbol,
                    MomentumPercent = momentum,
                    AverageVolume = averageVolume,
                    VolumeTrendPercent = volumeTrend,
                    VolatilityPercent = volatility,
                    IsAboveSma5 = isAboveSma5,
                    Rsi = rsi
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
            Week52High = week52High.Week52High,
            DistanceFromWeek52HighPercent =
                week52High.DistanceFromWeek52HighPercent,
            Score = score
        };
    }
}