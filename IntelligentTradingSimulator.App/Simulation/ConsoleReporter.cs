using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class ConsoleReporter
{
    private readonly PortfolioAllocationService _allocationService;
    private readonly PortfolioContributionService _contributionService;

    public ConsoleReporter(
        PortfolioAllocationService allocationService,
        PortfolioContributionService contributionService)
    {
        _allocationService = allocationService;
        _contributionService = contributionService;
    }

    public void PrintMarketRanking(
        IReadOnlyList<ObservationResult> rankedResults)
    {
        Console.WriteLine();
        Console.WriteLine("===== MARKET RANKING =====");
        Console.WriteLine();

        foreach (var result in rankedResults)
        {
            Console.WriteLine(
                $"{result.Symbol}" +
                $" | Momentum={result.MomentumPercent:N2}%" +
                $" | VolumeTrend={result.VolumeTrendPercent:N2}%" +
                $" | Volatility={result.VolatilityPercent:N2}%" +
                $" | AboveSMA5={result.IsAboveSma5}" +
                $" | Score={result.Score:N2}");
        }
    }

    public void PrintRecommendations(
        IReadOnlyList<StockRecommendation> recommendations)
    {
        Console.WriteLine();
        Console.WriteLine("===== PORTFOLIO RECOMMENDATION =====");
        Console.WriteLine();

        foreach (var recommendation in recommendations)
        {
            Console.WriteLine(
                $"{recommendation.Symbol}" +
                $" | Allocation={recommendation.AllocationPercent:N2}%" +
                $" | Score={recommendation.Score:N2}");
        }
    }

    public void PrintExistingPortfolio(Portfolio portfolio)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Using existing PortfolioId={portfolio.Id}");

        Console.WriteLine(
            $"Cash={portfolio.Cash:N2}");

        foreach (var position in portfolio.Positions)
        {
            Console.WriteLine(
                $"{position.Symbol} | Qty={position.Quantity:N4}");
        }
    }

    public void PrintPortfolioPerformance(
        PortfolioPerformance performance)
    {
        Console.WriteLine();
        Console.WriteLine("===== PORTFOLIO PERFORMANCE =====");

        Console.WriteLine(
            $"Portfolio Value: {performance.PortfolioValue:N2}");

        Console.WriteLine(
            $"Profit/Loss: {performance.ProfitLoss:N2}");

        Console.WriteLine(
            $"Return %: {performance.ReturnPercent:N2}%");
    }

    public void PrintPortfolioAllocation(
        Portfolio portfolio,
        decimal totalValue)
    {
        Console.WriteLine();
        Console.WriteLine("===== PORTFOLIO ALLOCATION =====");

        foreach (var position in portfolio.Positions)
        {
            var value = position.Quantity * position.CurrentPrice;
            var weight = value / totalValue * 100m;

            Console.WriteLine(
                $"{position.Symbol} | " +
                $"Value={value:N2} | " +
                $"Weight={weight:N2}%");
        }

        Console.WriteLine(
            $"Cash | Weight={(portfolio.Cash / totalValue * 100m):N2}%");
    }

    public void PrintTargetAllocation(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations)
    {
        Console.WriteLine();
        Console.WriteLine("===== TARGET ALLOCATION =====");

        foreach (var recommendation in recommendations)
        {
            var targetValue =
                _allocationService.CalculateTargetValue(portfolio, recommendation);

            var position =
                portfolio.Positions
                    .FirstOrDefault(x => x.Symbol == recommendation.Symbol);

            var currentValue =
                position is not null
                    ? position.Quantity * position.CurrentPrice
                    : 0m;

            var difference = targetValue - currentValue;

            Console.WriteLine(
                $"{recommendation.Symbol}" +
                $" | Current={currentValue:N2}" +
                $" | Target={targetValue:N2}" +
                $" | Diff={difference:N2}");
        }
    }

    public void PrintRebalanceByWeight(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations)
    {
        Console.WriteLine();
        Console.WriteLine("===== REBALANCE BY WEIGHT =====");

        foreach (var recommendation in recommendations)
        {
            var difference =
                _allocationService.CalculateDifference(portfolio, recommendation);

            if (difference > -100m)
            {
                continue;
            }

            Console.WriteLine(
                $"SELL {recommendation.Symbol} " +
                $"{Math.Abs(difference):N2}");
        }

        foreach (var recommendation in recommendations)
        {
            var difference =
                _allocationService.CalculateDifference(portfolio, recommendation);

            if (difference < 100m)
            {
                continue;
            }

            Console.WriteLine(
                $"BUY {recommendation.Symbol} " +
                $"{difference:N2}");
        }
    }

    public void PrintPositionContributions(Portfolio portfolio)
    {
        Console.WriteLine();
        Console.WriteLine("===== POSITION CONTRIBUTION =====");

        var contributions = _contributionService.Calculate(portfolio);

        foreach (var contribution in contributions)
        {
            Console.WriteLine(
                $"{contribution.Symbol}" +
                $" | P/L={contribution.ProfitLoss:N2}" +
                $" | Return={contribution.ReturnPercent:N2}%");
        }
    }

    public void PrintPreviousRunInfo(ObservationRun? previousRun)
    {
        if (previousRun is null)
        {
            return;
        }

        Console.WriteLine(
            $"RunId: {previousRun.Id}");

        Console.WriteLine(
            $"CreatedAt: {previousRun.CreatedAt}");
    }

    public void PrintBacktestSummary(BacktestSummaryReportResult result)
    {
        Console.WriteLine();
        Console.WriteLine("===== BACKTEST SUMMARY =====");

        if (!result.IsSuccess)
        {
            Console.WriteLine($"Error: {result.ErrorMessage}");
            return;
        }

        Console.WriteLine($"Run: {result.RunName} (Id={result.BacktestRunId})");
        Console.WriteLine($"Start Capital:  {result.StartCapital:N2}");
        Console.WriteLine($"Final Capital:  {result.FinalCapital:N2}");
        Console.WriteLine($"Profit:         {result.Profit:N2}");
        Console.WriteLine($"Return:         {result.ReturnPercent:N2}%");
        Console.WriteLine($"Trades:         {result.TradesCount}");
        Console.WriteLine($"Rebalances:     {result.RebalancesCount}");

        if (result.BenchmarkComparisons.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Benchmark Comparison:");

        foreach (var comparison in result.BenchmarkComparisons)
        {
            Console.WriteLine(
                $"{comparison.Symbol}" +
                $" | Strategy Return={comparison.StrategyReturnPercent:N2}%" +
                $" | Benchmark Return={comparison.BenchmarkReturnPercent:N2}%" +
                $" | Relative Return={comparison.RelativeReturnPercent:N2}%" +
                $" | Alpha={comparison.AlphaPercent:N2}%" +
                $" | Outcome={comparison.Outcome}");
        }
    }
}
