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
    private readonly RelativeStrengthCalculator _relativeStrengthCalculator = new();
    private readonly AtrCalculator _atrCalculator = new();

    public MarketObservationAnalyzer(
        StrategyConfiguration? strategyConfiguration = null)
        : this(
            ScoringEngineFactory.Create(strategyConfiguration ?? new StrategyConfiguration()),
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
        IReadOnlyList<MarketCandle> candles,
        FundamentalData? fundamentalData = null)
    {
        return Analyze(
            symbol,
            candles,
            candles,
            benchmarkCandles: null,
            benchmarkSymbol: null,
            analysisDate: null,
            fundamentalData: fundamentalData);
    }

    public ObservationResult Analyze(
        string symbol,
        IReadOnlyList<MarketCandle> candles,
        IReadOnlyList<MarketCandle> week52HighCandles,
        IReadOnlyList<MarketCandle>? benchmarkCandles = null,
        string? benchmarkSymbol = null,
        DateTime? analysisDate = null,
        FundamentalData? fundamentalData = null)
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
            _week52HighCalculator.Calculate(week52HighCandles);

        var relativeStrength = _relativeStrengthCalculator.Calculate(
            candles,
            benchmarkCandles,
            first.Date,
            analysisDate ?? last.Date);

        var features = new StockRankingFeatures
        {
            Symbol = symbol,
            AnalysisDate = DateOnly.FromDateTime(analysisDate ?? last.Date),
            MomentumPercent = momentum,
            AverageVolume = averageVolume,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5,
            Rsi = rsi,
            Atr = _atrCalculator.Calculate(candles),
            Week52High = week52High.Week52High,
            DistanceFromWeek52HighPercent = week52High.DistanceFromWeek52HighPercent,
            RelativeStrengthBenchmarkSymbol = benchmarkSymbol,
            RelativeStrengthPercent = relativeStrength,
            Fundamentals = fundamentalData
        };

        var score =
            _scoringEngine.CalculateScore(
                features.ToScoringContext(),
                out var relativeStrengthScoreContribution,
                out var factorContributions);

        return new ObservationResult
        {
            Features = features,
            Symbol = symbol,
            MomentumPercent = momentum,
            AverageVolume = averageVolume,
            VolumeTrendPercent = volumeTrend,
            VolatilityPercent = volatility,
            IsAboveSma5 = isAboveSma5,
            Rsi = rsi,
            Atr = features.Atr,
            Week52High = week52High.Week52High,
            DistanceFromWeek52HighPercent =
                week52High.DistanceFromWeek52HighPercent,
            RelativeStrengthBenchmarkSymbol = benchmarkSymbol,
            RelativeStrengthPercent = relativeStrength,
            RelativeStrengthScoreContribution = relativeStrengthScoreContribution,
            FactorContributions = factorContributions,
            Fundamentals = fundamentalData,
            Score = score
        };
    }
}