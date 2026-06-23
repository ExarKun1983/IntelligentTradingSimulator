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

    public PortfolioWorkflow(
        PortfolioRepository portfolioRepository,
        PortfolioSnapshotRepository portfolioSnapshotRepository,
        PositionSnapshotRepository positionSnapshotRepository,
        PortfolioExecutionService executionService,
        TradingEngine tradingEngine,
        PortfolioAllocationEngine allocationEngine,
        PortfolioRebalancingService rebalancingService,
        PortfolioPerformanceService performanceService,
        ConsoleReporter reporter)
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
    }

    public async Task<PortfolioState> RunAsync(
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices)
    {
        var portfolio = await _portfolioRepository.GetLatestAsync();

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

        await _portfolioRepository.SaveAsync(portfolio);

        _rebalancingService.Execute(
            portfolio,
            recommendations,
            prices,
            _allocationEngine,
            _tradingEngine);

        await _portfolioRepository.SaveAsync(portfolio);

        var performance = _performanceService.Calculate(portfolio);

        await _portfolioSnapshotRepository.SaveAsync(
            new PortfolioSnapshot
            {
                PortfolioId = portfolio.Id,
                Timestamp = DateTime.UtcNow,
                PortfolioValue = Math.Round(performance.PortfolioValue, 2),
                Cash = Math.Round(portfolio.Cash, 2),
                ProfitLoss = Math.Round(performance.ProfitLoss, 2),
                ReturnPercent = Math.Round(performance.ReturnPercent, 2)
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
                    PositionValue = x.Quantity * x.CurrentPrice,
                    Weight = x.Quantity * x.CurrentPrice / totalValue * 100m
                })
                .ToList();

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

        await _portfolioRepository.SaveAsync(portfolio);

        return portfolio;
    }
}
