using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestDayProcessor : IBacktestDayProcessor
{
    private readonly BacktestHistoricalMarketDataService _historicalDataService;
    private readonly MarketObservationAnalyzerFactory _analyzerFactory;
    private readonly PortfolioRecommendationService _recommendationService;
    private readonly PortfolioRebalancingService _rebalancingService;
    private readonly PortfolioAllocationEngine _allocationEngine;
    private readonly TradingEngine _tradingEngine;
    private readonly PortfolioPerformanceService _performanceService;
    private readonly IBacktestSnapshotRepository _snapshotRepository;
    private readonly IFeatureSnapshotRepository _featureSnapshotRepository;
    private readonly IWorkflowProgress _progress;
    private int? _partialDataWarningRunId;
    private int? _benchmarkWarningRunId;

    public BacktestDayProcessor(
        BacktestHistoricalMarketDataService historicalDataService,
        MarketObservationAnalyzerFactory analyzerFactory,
        PortfolioRecommendationService recommendationService,
        PortfolioRebalancingService rebalancingService,
        PortfolioAllocationEngine allocationEngine,
        TradingEngine tradingEngine,
        PortfolioPerformanceService performanceService,
        IBacktestSnapshotRepository snapshotRepository,
        IFeatureSnapshotRepository featureSnapshotRepository,
        IWorkflowProgress? progress = null)
    {
        _historicalDataService = historicalDataService;
        _analyzerFactory = analyzerFactory;
        _recommendationService = recommendationService;
        _rebalancingService = rebalancingService;
        _allocationEngine = allocationEngine;
        _tradingEngine = tradingEngine;
        _performanceService = performanceService;
        _snapshotRepository = snapshotRepository;
        _featureSnapshotRepository = featureSnapshotRepository;
        _progress = progress ?? NullWorkflowProgress.Instance;
    }

    public async Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
    {
        _progress.Context = _progress.Context with
        {
            Date = request.CurrentDate.Date,
            RunId = request.BacktestRunId,
            StrategyId = request.StrategyConfiguration.Id
        };
        var analyzer =
            _analyzerFactory.Create(request.StrategyConfiguration);
        var observationResults = new List<ObservationResult>();
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<MarketCandle> benchmarkCandles = [];
        var benchmarkSymbol = request.StrategyConfiguration.UseRelativeStrength
            ? request.StrategyConfiguration.RelativeStrengthBenchmarkSymbol
            : null;

        if (benchmarkSymbol is not null)
        {
            try
            {
                using var operation = _progress.BeginOperation("Fetching benchmark data", benchmarkSymbol);
                benchmarkCandles = await _historicalDataService.GetHistoricalDailyUpToAsync(
                    benchmarkSymbol,
                    request.HistoricalStartDate,
                    request.CurrentDate,
                    request.CurrentDate);
                if (benchmarkCandles.Count == 0)
                {
                    ReportBenchmarkUnavailable(benchmarkSymbol, request.BacktestRunId);
                }
            }
            catch (Exception)
            {
                Console.WriteLine($"Backtest: benchmark data unavailable for {benchmarkSymbol}.");
                ReportBenchmarkUnavailable(benchmarkSymbol, request.BacktestRunId);
            }
        }

        var riskExitsEnabled = request.StrategyConfiguration.StopLossPercent.HasValue ||
            request.StrategyConfiguration.TrailingStopPercent.HasValue;
        var symbols = (riskExitsEnabled
            ? request.Symbols.Concat(request.Portfolio.Positions.Select(x => x.Symbol))
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : request.Symbols).ToList();
        var succeeded = 0;
        var skipped = 0;
        var insufficientHistorySkipped = 0;
        var failed = 0;
        _progress.Context = _progress.Context with { CompletedSymbols = 0, TotalSymbols = symbols.Count };

        foreach (var symbol in symbols)
        {
            try
            {
                // Pass CurrentDate as both the fetch end-date and the look-ahead cutoff
                // so that no data beyond the simulated day is ever returned.
                IReadOnlyList<MarketCandle> candles;
                using (_progress.BeginOperation("Fetching price data", symbol))
                {
                    candles = await _historicalDataService.GetHistoricalDailyUpToAsync(
                        symbol,
                        MarketHistoryRequirements.GetEffectiveStartDate(
                            request.HistoricalStartDate,
                            request.CurrentDate),
                        request.CurrentDate,
                        request.CurrentDate);
                }

                var historicalCandles =
                    candles.OrderBy(x => x.Date).ToList();
                var usableCandles =
                    historicalCandles
                        .Where(x =>
                            x.Date.Date >=
                            request.HistoricalStartDate.Date)
                        .ToList();

                if (riskExitsEnabled && usableCandles.Count > 0)
                {
                    prices[symbol] = usableCandles.Last().Close;
                }

                if (usableCandles.Count < 5)
                {
                    skipped++;
                    insufficientHistorySkipped++;
                    continue;
                }

                if (!request.Symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                observationResults.Add(
                    analyzer.Analyze(
                        symbol,
                        usableCandles,
                        historicalCandles,
                        benchmarkCandles,
                        benchmarkSymbol,
                        request.CurrentDate));

                prices[symbol] = usableCandles.Last().Close;
                succeeded++;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Backtest: skipping {symbol} on {request.CurrentDate:yyyy-MM-dd} — {ex.Message}");
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

        if ((insufficientHistorySkipped > 0 || failed > 0) &&
            _partialDataWarningRunId != request.BacktestRunId)
        {
            _partialDataWarningRunId = request.BacktestRunId;
            _progress.Report(
                $"Partial market data: {succeeded} analyzed, {insufficientHistorySkipped} skipped, {failed} failed",
                force: true);
        }

        List<ObservationResult> rankedResults;
        IReadOnlyList<StockRecommendation> recommendations;
        using (_progress.BeginOperation("Building rankings"))
        {
            rankedResults = observationResults
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ToList();

            recommendations = _recommendationService.Build(rankedResults);
        }

        using (_progress.BeginOperation("Saving feature snapshots"))
        {
            await _featureSnapshotRepository.SaveAsync(
                rankedResults, request.StrategyConfiguration, request.BacktestRunId);
        }

        var portfolio = request.Portfolio;
        BacktestSnapshot snapshot;

        using (_progress.BeginOperation("Processing portfolio"))
        {
            foreach (var position in portfolio.Positions)
            {
                if (prices.TryGetValue(position.Symbol, out var currentPrice))
                {
                    position.CurrentPrice = currentPrice;
                }
            }

            var tradesBeforeRebalance = portfolio.Trades.Count;
            var rebalanceExecuted = false;

            if (recommendations.Count > 0 || riskExitsEnabled)
            {
                _rebalancingService.Execute(
                    portfolio,
                    recommendations,
                    prices,
                    _allocationEngine,
                    _tradingEngine,
                    request.StrategyConfiguration.StopLossPercent,
                    request.StrategyConfiguration.TrailingStopPercent);

                rebalanceExecuted = recommendations.Count > 0;
            }

            var tradesExecutedToday = portfolio.Trades.Count - tradesBeforeRebalance;

            var performance = _performanceService.Calculate(portfolio);

            snapshot = new BacktestSnapshot
            {
                BacktestRunId = request.BacktestRunId,
                Date = request.CurrentDate.Date,
                PortfolioValue = Math.Round(performance.PortfolioValue, 4),
                Cash = Math.Round(portfolio.Cash, 4),
                ProfitLoss = Math.Round(performance.ProfitLoss, 4),
                ReturnPercent = Math.Round(performance.ReturnPercent, 4),
                TradesExecuted = tradesExecutedToday,
                RebalanceExecuted = rebalanceExecuted
            };
        }

        using (_progress.BeginOperation("Saving daily snapshot"))
        {
            await _snapshotRepository.AddAsync(snapshot);
        }

        return new BacktestDayResult
        {
            CurrentDate = request.CurrentDate.Date,
            Portfolio = portfolio,
            Snapshot = snapshot,
            Recommendations = recommendations,
            Observations = rankedResults
        };
    }

    private void ReportBenchmarkUnavailable(string symbol, int runId)
    {
        if (_benchmarkWarningRunId == runId)
        {
            return;
        }

        _benchmarkWarningRunId = runId;
        _progress.Report($"Benchmark data unavailable for {symbol}", force: true);
    }
}
