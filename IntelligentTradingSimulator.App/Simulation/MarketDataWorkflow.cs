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

    public MarketDataWorkflow(
        IMarketDataProvider provider,
        MarketObservationAnalyzer analyzer,
        PortfolioRecommendationService recommendationService,
        ObservationResultRepository observationResultRepository)
    {
        _provider = provider;
        _analyzer = analyzer;
        _recommendationService = recommendationService;
        _observationResultRepository = observationResultRepository;
    }

    public async Task<MarketDataResult> CollectAndAnalyzeAsync(
        IEnumerable<string> symbols,
        DateTime from)
    {
        var results = new List<ObservationResult>();
        var prices = new Dictionary<string, decimal>();

        foreach (var symbol in symbols)
        {
            try
            {
                var candles =
                    await _provider.GetHistoricalDailyAsync(symbol, from);

                var usableCandles =
                    candles.OrderBy(x => x.Date).ToList();

                if (usableCandles.Count < 5)
                {
                    Console.WriteLine(
                        $"Pomijam {symbol} - za mało świeczek: {usableCandles.Count}");

                    continue;
                }

                results.Add(_analyzer.Analyze(symbol, usableCandles));

                prices[symbol] = usableCandles.Last().Close;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Błąd pobierania danych dla {symbol}: {ex.Message}");
            }
        }

        var rankedResults =
            results.OrderByDescending(x => x.Score).ToList();

        var recommendations =
            _recommendationService.Build(rankedResults);

        await _observationResultRepository.SaveAsync(rankedResults);

        var previousRun =
            await _observationResultRepository.GetPreviousRunAsync();

        return new MarketDataResult(rankedResults, recommendations, prices, previousRun);
    }
}
