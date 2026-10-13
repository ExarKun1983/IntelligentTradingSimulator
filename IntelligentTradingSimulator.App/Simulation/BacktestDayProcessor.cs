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

    public BacktestDayProcessor(
        BacktestHistoricalMarketDataService historicalDataService,
        MarketObservationAnalyzerFactory analyzerFactory,
        PortfolioRecommendationService recommendationService,
        PortfolioRebalancingService rebalancingService,
        PortfolioAllocationEngine allocationEngine,
        TradingEngine tradingEngine,
        PortfolioPerformanceService performanceService,
        IBacktestSnapshotRepository snapshotRepository)
    {
        _historicalDataService = historicalDataService;
        _analyzerFactory = analyzerFactory;
        _recommendationService = recommendationService;
        _rebalancingService = rebalancingService;
        _allocationEngine = allocationEngine;
        _tradingEngine = tradingEngine;
        _performanceService = performanceService;
        _snapshotRepository = snapshotRepository;
    }

    public async Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
    {
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
                benchmarkCandles = await _historicalDataService.GetHistoricalDailyUpToAsync(
                    benchmarkSymbol,
                    request.HistoricalStartDate,
                    request.CurrentDate,
                    request.CurrentDate);
            }
            catch (Exception)
            {
                Console.WriteLine($"Backtest: benchmark data unavailable for {benchmarkSymbol}.");
            }
        }

        var riskExitsEnabled = request.StrategyConfiguration.StopLossPercent.HasValue ||
            request.StrategyConfiguration.TrailingStopPercent.HasValue;
        var symbols = riskExitsEnabled
            ? request.Symbols.Concat(request.Portfolio.Positions.Select(x => x.Symbol))
                .Distinct(StringComparer.OrdinalIgnoreCase)
            : request.Symbols;

        foreach (var symbol in symbols)
        {
            try
            {
                // Pass CurrentDate as both the fetch end-date and the look-ahead cutoff
                // so that no data beyond the simulated day is ever returned.
                var candles =
                    await _historicalDataService.GetHistoricalDailyUpToAsync(
                        symbol,
                        MarketHistoryRequirements.GetEffectiveStartDate(
                            request.HistoricalStartDate,
                            request.CurrentDate),
                        request.CurrentDate,
                        request.CurrentDate);

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

                if (usableCandles.Count < 5 ||
                    !request.Symbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
                {
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
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"Backtest: skipping {symbol} on {request.CurrentDate:yyyy-MM-dd} — {ex.Message}");
            }
        }

        var rankedResults =
            observationResults
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                .ToList();

        var recommendations = _recommendationService.Build(rankedResults);

        var portfolio = request.Portfolio;

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

        var snapshot = new BacktestSnapshot
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

        await _snapshotRepository.AddAsync(snapshot);

        return new BacktestDayResult
        {
            CurrentDate = request.CurrentDate.Date,
            Portfolio = portfolio,
            Snapshot = snapshot,
            Recommendations = recommendations,
            Observations = rankedResults
        };
    }
}
