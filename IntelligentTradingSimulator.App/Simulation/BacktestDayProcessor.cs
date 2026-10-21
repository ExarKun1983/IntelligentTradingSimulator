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
    private readonly PortfolioAllocationService _allocationService;
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
        PortfolioRebalancingService rebalancingService,
        PortfolioAllocationService allocationService,
        TradingEngine tradingEngine,
        PortfolioPerformanceService performanceService,
        IBacktestSnapshotRepository snapshotRepository,
        IFeatureSnapshotRepository featureSnapshotRepository,
        IWorkflowProgress? progress = null)
    {
        _historicalDataService = historicalDataService;
        _analyzerFactory = analyzerFactory;
        _allocationService = allocationService;
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
        var recommendationService =
            new PortfolioRecommendationService(request.StrategyConfiguration.MaxPositions);
        var allocationEngine =
            new PortfolioAllocationEngine(_allocationService, request.StrategyConfiguration.MaxPositionWeight);
        var observationResults = new List<ObservationResult>();
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var opens = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var histories = new Dictionary<string, (List<MarketCandle> Usable, List<MarketCandle> Historical)>(
            StringComparer.OrdinalIgnoreCase);
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

        var symbols = request.Symbols.Concat(request.Portfolio.Positions.Select(x => x.Symbol))
            .Concat(request.ExecutionState.Pending.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

                var currentCandle = historicalCandles.LastOrDefault(x => x.Date.Date == request.CurrentDate.Date);
                if (currentCandle is null)
                {
                    skipped++;
                    continue;
                }

                if (TradingPrecisionPolicy.TryNormalizePrice(currentCandle.Open, out var open))
                {
                    opens[symbol] = open;
                }
                if (!TradingPrecisionPolicy.TryNormalizePrice(currentCandle.Close, out var close))
                {
                    throw new InvalidOperationException("The current candle has no valid closing price.");
                }
                prices[symbol] = close;

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

                histories[symbol] = (usableCandles, historicalCandles);
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

        var portfolio = request.Portfolio;
        var previousPrices = portfolio.Positions.ToDictionary(
            x => x.Symbol, x => x.CurrentPrice, StringComparer.OrdinalIgnoreCase);
        var tradesBeforeOpen = portfolio.Trades.Count;
        using (_progress.BeginOperation("Executing pending opening intents"))
        {
            request.ExecutionState.ExecuteOpen(
                portfolio, request.CurrentDate, opens, allocationEngine, _tradingEngine);
        }

        using (_progress.BeginOperation("Marking closing prices"))
        {
            foreach (var position in portfolio.Positions)
            {
                if (prices.TryGetValue(position.Symbol, out var close))
                {
                    position.CurrentPrice = close;
                }
                else if (previousPrices.TryGetValue(position.Symbol, out var previousPrice))
                {
                    position.CurrentPrice = previousPrice;
                }
            }
        }

        foreach (var (symbol, history) in histories)
        {
            try
            {
                observationResults.Add(analyzer.Analyze(
                    symbol, history.Usable, history.Historical,
                    benchmarkCandles, benchmarkSymbol, request.CurrentDate));
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Backtest: skipping {symbol} on {request.CurrentDate:yyyy-MM-dd} — {ex.Message}");
            }
        }

        List<ObservationResult> rankedResults;
        IReadOnlyList<StockRecommendation> recommendations;
        using (_progress.BeginOperation("Building rankings"))
        {
            rankedResults = observationResults
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ToList();

            recommendations = request.ExecutionState.BuildRecommendations(
                rankedResults, request.Symbols, recommendationService);
            request.ExecutionState.GenerateAfterClose(
                portfolio, request.CurrentDate, prices, recommendations, rankedResults, request.StrategyConfiguration);
        }

        using (_progress.BeginOperation("Saving feature snapshots"))
        {
            await _featureSnapshotRepository.SaveAsync(
                rankedResults, request.StrategyConfiguration, request.BacktestRunId);
        }

        BacktestSnapshot snapshot;

        using (_progress.BeginOperation("Processing portfolio"))
        {
            var openingTrades = portfolio.Trades.Skip(tradesBeforeOpen).ToList();
            var tradesExecutedToday = openingTrades.Count;
            var rebalanceExecuted = openingTrades.Any(
                x => x.Action == Core.Enums.TradeActionType.Buy || x.ExitReason == Core.Enums.ExitReason.Rebalance);

            var performance = _performanceService.Calculate(portfolio);

            snapshot = new BacktestSnapshot
            {
                BacktestRunId = request.BacktestRunId,
                Date = request.CurrentDate.Date,
                PortfolioValue = TradingPrecisionPolicy.RoundReport(performance.PortfolioValue),
                Cash = TradingPrecisionPolicy.RoundReport(portfolio.Cash),
                ProfitLoss = TradingPrecisionPolicy.RoundReport(performance.ProfitLoss),
                ReturnPercent = TradingPrecisionPolicy.RoundReport(performance.ReturnPercent),
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
