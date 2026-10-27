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
    private readonly IFeatureSnapshotRepository _featureSnapshotRepository;
    private readonly StrategyConfiguration _strategyConfiguration;
    private readonly BacktestHistoricalMarketDataService _historicalDataService;
    private readonly IFundamentalDataProvider? _fundamentalDataProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowProgress _progress;

    public MarketDataWorkflow(
        IMarketDataProvider provider,
        MarketObservationAnalyzer analyzer,
        PortfolioRecommendationService recommendationService,
        ObservationResultRepository observationResultRepository,
        IFeatureSnapshotRepository featureSnapshotRepository,
        StrategyConfiguration? strategyConfiguration = null,
        IFundamentalDataProvider? fundamentalDataProvider = null,
        TimeProvider? timeProvider = null,
        IWorkflowProgress? progress = null)
    {
        _provider = provider;
        _analyzer = analyzer;
        _recommendationService = recommendationService;
        _observationResultRepository = observationResultRepository;
        _featureSnapshotRepository = featureSnapshotRepository;
        _strategyConfiguration = strategyConfiguration ?? new StrategyConfiguration();
        _historicalDataService = new BacktestHistoricalMarketDataService(provider);
        _fundamentalDataProvider = fundamentalDataProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _progress = progress ?? NullWorkflowProgress.Instance;
    }

    public async Task<MarketDataResult> CollectAndAnalyzeAsync(
        IEnumerable<string> symbols,
        DateTime from,
        System.Threading.CancellationToken cancellationToken = default)
    {
        var results = new List<ObservationResult>();
        var prices = new Dictionary<string, decimal>();
        var analysisDate = _timeProvider.GetLocalNow().Date;
        var symbolList = symbols.ToList();
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        var unavailableFundamentals = 0;
        _progress.Context = _progress.Context with
        {
            Workflow = "market-data",
            StrategyId = _strategyConfiguration.Id,
            RunId = null,
            Date = analysisDate,
            CompletedDates = null,
            TotalDates = null,
            CompletedSymbols = 0,
            TotalSymbols = symbolList.Count,
            VariantIndex = null,
            TotalVariants = null
        };
        _progress.Report(_fundamentalDataProvider is null
            ? "Fundamentals unavailable: no provider configured"
            : "Collecting market data", force: true);
        IReadOnlyList<MarketCandle> benchmarkCandles = [];
        var benchmarkSymbol = _strategyConfiguration.UseRelativeStrength
            ? _strategyConfiguration.RelativeStrengthBenchmarkSymbol
            : null;

        if (benchmarkSymbol is not null)
        {
            try
            {
                using var operation = _progress.BeginOperation("Fetching benchmark data", benchmarkSymbol);
                benchmarkCandles = await _historicalDataService.GetHistoricalDailyUpToAsync(
                    benchmarkSymbol, from, analysisDate, analysisDate);
                if (benchmarkCandles.Count == 0)
                {
                    Console.WriteLine($"Benchmark data unavailable for {benchmarkSymbol}.");
                    _progress.Report($"Benchmark data unavailable for {benchmarkSymbol}", force: true);
                }
            }
            catch (Exception)
            {
                Console.WriteLine($"Benchmark data unavailable for {benchmarkSymbol}.");
                _progress.Report($"Benchmark data unavailable for {benchmarkSymbol}", force: true);
            }
        }

        foreach (var symbol in symbolList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IReadOnlyList<MarketCandle> candles;
                using (_progress.BeginOperation("Fetching price data", symbol))
                {
                    candles = await _provider.GetHistoricalDailyAsync(
                        symbol,
                        MarketHistoryRequirements.GetEffectiveStartDate(
                            from,
                            analysisDate));
                }

                var historicalCandles =
                    candles
                        .Where(x => x.Date.Date <= analysisDate)
                        .OrderBy(x => x.Date).ToList();
                var usableCandles =
                    historicalCandles
                        .Where(x => x.Date.Date >= from.Date)
                        .ToList();

                if (usableCandles.Count > 0)
                {
                    prices[symbol] = usableCandles.Last().Close;
                }

                if (usableCandles.Count < 5)
                {
                    Console.WriteLine(
                        $"Pomijam {symbol} - za mało świeczek: {usableCandles.Count}");
                    skipped++;
                    continue;
                }

                FundamentalData? fundamentalData = null;
                if (_fundamentalDataProvider is not null)
                {
                    try
                    {
                        using var operation = _progress.BeginOperation("Fetching fundamental data", symbol);
                        fundamentalData =
                            await _fundamentalDataProvider.GetFundamentalDataAsync(symbol, cancellationToken);
                        if (fundamentalData?.AsOfDate > DateOnly.FromDateTime(analysisDate))
                            fundamentalData = null;
                        if (fundamentalData is null)
                        {
                            unavailableFundamentals++;
                            _progress.Report($"Fundamentals unavailable for {symbol}; continuing without fundamentals");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("Market data collection canceled by caller.", cancellationToken);
                    }
                    catch (FundamentalDataFetchException ex)
                    {
                        unavailableFundamentals++;
                        foreach (var failure in ex.Failures)
                        {
                            ReportFundamentalWarning(
                                $"Failed to fetch fundamental data for {symbol}; {failure}. Continuing without fundamentals.");
                        }
                    }
                    catch (Exception)
                    {
                        unavailableFundamentals++;
                        ReportFundamentalWarning(
                            $"Failed to fetch fundamental data for {symbol}; Provider=unknown; Endpoint=unknown; HTTP=unknown; Category=Unknown. Continuing without fundamentals.");
                    }
                }

                results.Add(
                    _analyzer.Analyze(
                        symbol,
                        usableCandles,
                        historicalCandles,
                        benchmarkCandles,
                        benchmarkSymbol,
                        analysisDate,
                        fundamentalData));

                prices[symbol] = usableCandles.Last().Close;
                succeeded++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Market data collection canceled by caller.", cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Błąd pobierania danych dla {symbol}: {ex.Message}");
                failed++;
            }
            finally
            {
                _progress.Context = _progress.Context with
                {
                    CompletedSymbols = succeeded + skipped + failed
                };
                _progress.Report($"Symbols processed: {succeeded} analyzed, {skipped} skipped, {failed} failed");
            }
        }
        _progress.Report($"Symbols processed: {succeeded} analyzed, {skipped} skipped, {failed} failed", force: true);
        if (unavailableFundamentals > 0)
        {
            ReportFundamentalWarning(
                $"Fundamentals unavailable for {unavailableFundamentals} symbols; Continuing without fundamentals");
        }

        List<ObservationResult> rankedResults;
        IReadOnlyList<StockRecommendation> recommendations;
        using (_progress.BeginOperation("Building rankings"))
        {
            rankedResults = results
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ToList();

            recommendations = _recommendationService.BuildCandidates(rankedResults);
        }

        using (_progress.BeginOperation("Saving observations"))
        {
            await _observationResultRepository.SaveAsync(rankedResults);
        }
        using (_progress.BeginOperation("Saving feature snapshots"))
        {
            await _featureSnapshotRepository.SaveAsync(rankedResults, _strategyConfiguration);
        }

        using var previousRunOperation = _progress.BeginOperation("Loading previous run");
        var previousRun = await _observationResultRepository.GetPreviousRunAsync();

        return new MarketDataResult(rankedResults, recommendations, prices, previousRun);
    }

    private void ReportFundamentalWarning(string message)
    {
        if (_progress is NullWorkflowProgress)
            Console.WriteLine(message);
        else
            _progress.Report(message, force: true);
    }
}
