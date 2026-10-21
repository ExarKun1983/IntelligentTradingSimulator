using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        IWorkflowProgress? progress = null)
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
    }

    public async Task<PortfolioState> RunAsync(
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices)
    {
        using var operation = _progress.BeginOperation("Processing portfolio");
        Portfolio? portfolio;
        using (_progress.BeginOperation("Loading portfolio"))
            portfolio = await _portfolioRepository.GetLatestAsync();

        if (portfolio is null)
        {
            portfolio = await CreateInitialPortfolioAsync(recommendations, prices);
        }
        else
        {
            _reporter.PrintExistingPortfolio(portfolio);
        }

        foreach (var position in portfolio.Positions)
        {
            if (prices.TryGetValue(position.Symbol, out var currentPrice))
            {
                position.CurrentPrice = currentPrice;
            }
        }

        using (_progress.BeginOperation("Saving portfolio prices"))
            await _portfolioRepository.SaveAsync(portfolio);

        _rebalancingService.Execute(
            portfolio,
            recommendations,
            prices,
            _allocationEngine,
            _tradingEngine,
            _strategyConfiguration.StopLossPercent,
            _strategyConfiguration.TrailingStopPercent);

        using (_progress.BeginOperation("Saving rebalanced portfolio"))
            await _portfolioRepository.SaveAsync(portfolio);

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
                });

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
            await _positionSnapshotRepository.SaveAsync(positionSnapshots);

        return new PortfolioState(portfolio, performance, totalValue);
    }

    private async Task<Portfolio> CreateInitialPortfolioAsync(
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices)
    {
        Console.WriteLine();
        Console.WriteLine("===== CREATE INITIAL PORTFOLIO =====");

        var portfolio = new Portfolio
        {
            Name = "Main Portfolio",
            StartingCash = 10000m,
            Cash = 10000m
        };

        _executionService.ExecuteRecommendations(
            portfolio,
            recommendations,
            prices,
            _tradingEngine);

        using (_progress.BeginOperation("Saving initial portfolio"))
            await _portfolioRepository.SaveAsync(portfolio);

        return portfolio;
    }
}
