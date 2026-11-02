using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class PortfolioWorkflow
{
    private readonly PortfolioRepository _portfolioRepository;
    private readonly PortfolioSnapshotRepository _portfolioSnapshotRepository;
    private readonly PositionSnapshotRepository _positionSnapshotRepository;
    private readonly PortfolioExecutionService _executionService;
    private readonly TradingEngine _tradingEngine;
    private readonly PortfolioAllocationEngine _allocationEngine;
    private readonly PortfolioRebalancingService _rebalancingService;
    private readonly PortfolioPerformanceService _performanceService;
    private readonly ConsoleReporter _reporter;
    private readonly StrategyConfiguration _strategyConfiguration;
    private readonly IWorkflowProgress _progress;
    private readonly TimeProvider _timeProvider;

    public PortfolioWorkflow(
        PortfolioRepository portfolioRepository,
        PortfolioSnapshotRepository portfolioSnapshotRepository,
        PositionSnapshotRepository positionSnapshotRepository,
        PortfolioExecutionService executionService,
        TradingEngine tradingEngine,
        PortfolioAllocationEngine allocationEngine,
        PortfolioRebalancingService rebalancingService,
        PortfolioPerformanceService performanceService,
        ConsoleReporter reporter,
        StrategyConfiguration? strategyConfiguration = null,
        IWorkflowProgress? progress = null,
        TimeProvider? timeProvider = null)
    {
        _portfolioRepository = portfolioRepository;
        _portfolioSnapshotRepository = portfolioSnapshotRepository;
        _positionSnapshotRepository = positionSnapshotRepository;
        _executionService = executionService;
        _tradingEngine = tradingEngine;
        _allocationEngine = allocationEngine;
        _rebalancingService = rebalancingService;
        _performanceService = performanceService;
        _reporter = reporter;
        _strategyConfiguration = strategyConfiguration ?? new StrategyConfiguration();
        _progress = progress ?? NullWorkflowProgress.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PortfolioState> RunAsync(
        IReadOnlyList<StockRecommendation> recommendations,
        CompletedMarketSession session,
        CancellationToken cancellationToken = default)
    {
        var execution = await _portfolioRepository.ExecutePaperSessionAsync(
            session.SessionDate,
            portfolio =>
            {
                session.Validate(_timeProvider.GetUtcNow());
                if (portfolio.Positions.Any(x => !session.Prices.ContainsKey(x.Symbol)) ||
                    recommendations.Any(x => !session.Prices.ContainsKey(x.Symbol)))
                    throw new InvalidOperationException("Paper execution requires session prices for every held and recommended symbol.");
            },
            (portfolio, isNew, token) => ExecuteAsync(portfolio, isNew, recommendations, session.Prices, token),
            cancellationToken);
        return execution.Result is { } result
            ? result with { Outcome = execution.Outcome }
            : new PortfolioState(execution.Portfolio, _performanceService.Calculate(execution.Portfolio),
                execution.Portfolio.TotalValue) { Outcome = execution.Outcome };
    }

    private async Task<PortfolioState> ExecuteAsync(
        Portfolio portfolio,
        bool isNew,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices,
        CancellationToken cancellationToken)
    {
        using var operation = _progress.BeginOperation("Processing portfolio");
        if (isNew)
        {
            await CreateInitialPortfolioAsync(portfolio, recommendations, prices, cancellationToken);
        }
        else
        {
            _reporter.PrintExistingPortfolio(portfolio);
        }

        foreach (var position in portfolio.Positions)
        {
            if (prices.TryGetValue(position.Symbol, out var currentPrice)
                && TradingPrecisionPolicy.TryNormalizePrice(currentPrice, out var normalizedPrice))
            {
                position.CurrentPrice = normalizedPrice;
            }
        }

        using (_progress.BeginOperation("Saving portfolio prices"))
            await _portfolioRepository.SaveAsync(portfolio, cancellationToken);

        var executionTargets = _rebalancingService.Execute(
            portfolio,
            recommendations,
            prices,
            _allocationEngine,
            _tradingEngine,
            _strategyConfiguration.StopLossPercent,
            _strategyConfiguration.TrailingStopPercent,
            maxPositions: _strategyConfiguration.MaxPositions);

        using (_progress.BeginOperation("Saving rebalanced portfolio"))
            await _portfolioRepository.SaveAsync(portfolio, cancellationToken);

        var performance = _performanceService.Calculate(portfolio);

        using (_progress.BeginOperation("Saving portfolio snapshot"))
            await _portfolioSnapshotRepository.SaveAsync(
                new PortfolioSnapshot
                {
                    PortfolioId = portfolio.Id,
                    Timestamp = DateTime.UtcNow,
                    PortfolioValue = Math.Round(performance.PortfolioValue,
                        TradingPrecisionPolicy.SnapshotScale, MidpointRounding.AwayFromZero),
                    Cash = Math.Round(portfolio.Cash,
                        TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero),
                    ProfitLoss = Math.Round(performance.ProfitLoss,
                        TradingPrecisionPolicy.SnapshotScale, MidpointRounding.AwayFromZero),
                    ReturnPercent = Math.Round(performance.ReturnPercent,
                        TradingPrecisionPolicy.PercentageScale, MidpointRounding.AwayFromZero)
                }, executionSnapshot: true, cancellationToken);

        var totalValue =
            portfolio.Cash +
            portfolio.Positions.Sum(x => x.Quantity * x.CurrentPrice);

        var positionSnapshots =
            portfolio.Positions
                .Select(x => new PositionSnapshot
                {
                    PortfolioId = portfolio.Id,
                    Timestamp = DateTime.UtcNow,
                    Symbol = x.Symbol,
                    Quantity = x.Quantity,
                    CurrentPrice = x.CurrentPrice,
                    PositionValue = Math.Round(x.Quantity * x.CurrentPrice,
                        TradingPrecisionPolicy.SnapshotScale, MidpointRounding.AwayFromZero),
                    Weight = Math.Round(x.Quantity * x.CurrentPrice / totalValue * 100m,
                        TradingPrecisionPolicy.PercentageScale, MidpointRounding.AwayFromZero)
                })
                .ToList();

        using (_progress.BeginOperation("Saving position snapshots"))
            await _positionSnapshotRepository.SaveAsync(positionSnapshots, cancellationToken);

        return new PortfolioState(portfolio, performance, totalValue)
        {
            ExecutionTargets = executionTargets
        };
    }

    private async Task CreateInitialPortfolioAsync(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("===== CREATE INITIAL PORTFOLIO =====");

        _executionService.ExecuteRecommendations(
            portfolio,
            recommendations,
            prices,
            _tradingEngine);

        using (_progress.BeginOperation("Saving initial portfolio"))
            await _portfolioRepository.SaveAsync(portfolio, cancellationToken);
    }
}
