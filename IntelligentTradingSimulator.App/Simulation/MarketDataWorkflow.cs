using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class MarketDataWorkflow
{
    private readonly IMarketDataProvider _provider;
    private readonly MarketObservationAnalyzer _analyzer;
    private readonly PortfolioRecommendationService _recommendationService;
    private readonly ObservationResultRepository _observationResultRepository;
    private readonly StrategyConfiguration _strategyConfiguration;
    private readonly BacktestHistoricalMarketDataService _historicalDataService;

    public MarketDataWorkflow(
        IMarketDataProvider provider,
        MarketObservationAnalyzer analyzer,
        PortfolioRecommendationService recommendationService,
        ObservationResultRepository observationResultRepository,
        StrategyConfiguration? strategyConfiguration = null)
    {
        _provider = provider;
        _analyzer = analyzer;
        _recommendationService = recommendationService;
        _observationResultRepository = observationResultRepository;
        _strategyConfiguration = strategyConfiguration ?? new StrategyConfiguration();
        _historicalDataService = new BacktestHistoricalMarketDataService(provider);
    }

    public async Task<MarketDataResult> CollectAndAnalyzeAsync(
        IEnumerable<string> symbols,
        DateTime from)
    {
        var results = new List<ObservationResult>();
        var prices = new Dictionary<string, decimal>();
        var analysisDate = DateTime.Today;
        IReadOnlyList<MarketCandle> benchmarkCandles = [];
        var benchmarkSymbol = _strategyConfiguration.UseRelativeStrength
            ? _strategyConfiguration.RelativeStrengthBenchmarkSymbol
            : null;

        if (benchmarkSymbol is not null)
        {
            try
            {
                benchmarkCandles = await _historicalDataService.GetHistoricalDailyUpToAsync(
                    benchmarkSymbol, from, analysisDate, analysisDate);
            }
            catch (Exception)
            {
                Console.WriteLine($"Benchmark data unavailable for {benchmarkSymbol}.");
            }
        }

        foreach (var symbol in symbols)
        {
            try
            {
                var candles =
                    await _provider.GetHistoricalDailyAsync(
                        symbol,
                        MarketHistoryRequirements.GetEffectiveStartDate(
                            from,
                            analysisDate));

                var historicalCandles =
                    candles
                        .Where(x => x.Date.Date <= analysisDate)
                        .OrderBy(x => x.Date).ToList();
                var usableCandles =
                    historicalCandles
                        .Where(x => x.Date.Date >= from.Date)
                        .ToList();

                if (_strategyConfiguration.StopLossPercent.HasValue && usableCandles.Count > 0)
                {
                    prices[symbol] = usableCandles.Last().Close;
                }

                if (usableCandles.Count < 5)
                {
                    Console.WriteLine(
                        $"Pomijam {symbol} - za mało świeczek: {usableCandles.Count}");

                    continue;
                }

                results.Add(
                    _analyzer.Analyze(
                        symbol,
                        usableCandles,
                        historicalCandles,
                        benchmarkCandles,
                        benchmarkSymbol,
                        analysisDate));

                prices[symbol] = usableCandles.Last().Close;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Błąd pobierania danych dla {symbol}: {ex.Message}");
            }
        }

        var rankedResults =
            results
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ToList();

        var recommendations =
            _recommendationService.Build(rankedResults);

        await _observationResultRepository.SaveAsync(rankedResults);

        var previousRun =
            await _observationResultRepository.GetPreviousRunAsync();

        return new MarketDataResult(rankedResults, recommendations, prices, previousRun);
    }
}
