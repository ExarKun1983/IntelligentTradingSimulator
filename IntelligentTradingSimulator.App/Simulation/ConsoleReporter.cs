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
                $" | RSI={(result.Rsi.HasValue ? result.Rsi.Value.ToString("N2") : "N/A")}" +
                $" | ATR={(result.Atr.HasValue ? result.Atr.Value.ToString("N2") : "N/A")}" +
                $" | 52WHigh={(result.Week52High.HasValue ? result.Week52High.Value.ToString("N2") : "N/A")}" +
                $" | 52WDist={(result.DistanceFromWeek52HighPercent.HasValue ? result.DistanceFromWeek52HighPercent.Value.ToString("N2") + "%" : "N/A")}" +
                $" | RS[{result.RelativeStrengthBenchmarkSymbol ?? "N/A"}]={(result.RelativeStrengthPercent.HasValue ? result.RelativeStrengthPercent.Value.ToString("N2") + "pp" : "N/A")}" +
                $" | RSScore={(result.RelativeStrengthScoreContribution.HasValue ? result.RelativeStrengthScoreContribution.Value.ToString("N2") : "N/A")}" +
                $" | Score={result.Score:N2}");

            if (result.FactorContributions is null)
            {
                Console.WriteLine("  Score breakdown: N/A");
            }
            else
            {
                Console.WriteLine("  Score breakdown (points): " + string.Join(" | ",
                    result.FactorContributions.Select(factor =>
                        $"{factor.Factor}={(factor.Contribution.HasValue ? factor.Contribution.Value.ToString("G29") : "N/A")} [{factor.Status}]" +
                        (factor.Explanation is null ? "" : $" ({factor.Explanation})"))));
            }

            var fundamentals = result.Fundamentals;
            var asOfDate = fundamentals?.AsOfDate?.ToString("yyyy-MM-dd") ?? "N/A";
            var revenueGrowth = FormatPercent(fundamentals?.RevenueGrowthPercent);
            var epsGrowth = FormatPercent(fundamentals?.EpsGrowthPercent);
            var margins = FormatPercent(fundamentals?.Margins);
            var debt = FormatValue(fundamentals?.Debt);
            Console.WriteLine(
                $"  Fundamentals: AsOf={asOfDate}" +
                $" | RevGrowth={revenueGrowth}" +
                $" | EpsGrowth={epsGrowth}" +
                $" | Margins={margins}" +
                $" | Debt={debt}");
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

    private static string FormatPercent(decimal? value) =>
        value.HasValue ? $"{value.Value:N2}%" : "N/A";

    private static string FormatValue(decimal? value) =>
        value.HasValue ? value.Value.ToString("N2") : "N/A";

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

    public void PrintVariantComparisonReport(StrategyComparisonReport report)
    {
        Console.WriteLine();
        Console.WriteLine("===== STRATEGY COMPARISON REPORT =====");

        if (report.SortBy.HasValue)
        {
            var direction = report.SortBy == StrategyComparisonSortMetric.MaxDrawdownPercent
                ? "lowest first" : "highest first";
            Console.WriteLine($"Sorted by: {report.SortBy} ({direction})");
            if (report.BenchmarkSymbol is not null)
                Console.WriteLine($"Benchmark: {report.BenchmarkSymbol}");
            Console.WriteLine($"Top-ranked strategy: {report.TopStrategy?.StrategyId ?? "N/A"}");
        }

        if (report.Entries.Count == 0)
        {
            Console.WriteLine("No variant results to compare.");
            return;
        }

        foreach (var entry in report.Entries)
        {
            Console.WriteLine();
            Console.WriteLine($"Strategy:       {entry.StrategyId}");
            if (report.SortBy.HasValue)
                Console.WriteLine($"  Rank:         {entry.Rank?.ToString() ?? "N/A"}");

            if (!entry.IsSuccess)
            {
                Console.WriteLine($"  Status:       Failed");
                Console.WriteLine($"  Error:        {entry.ErrorMessage}");
                continue;
            }

            Console.WriteLine($"  Status:       Completed");
            Console.WriteLine($"  Start Capital:  {entry.StartCapital:N2}");
            Console.WriteLine($"  Final Capital:  {entry.FinalCapital:N2}");
            Console.WriteLine($"  Return:         {entry.ReturnPercent:N2}%");
            Console.WriteLine($"  Max Drawdown:   {entry.MaxDrawdownPercent:N2}%");
            Console.WriteLine($"  DD Duration:    {entry.DrawdownDurationDays} day(s)");
            Console.WriteLine(entry.RecoveryTimeDays.HasValue
                ? $"  Recovery Time:  {entry.RecoveryTimeDays.Value} day(s)"
                : entry.DrawdownDurationDays == 0
                    ? "  Recovery Time:  N/A (no drawdown)"
                    : "  Recovery Time:  N/A (not recovered)");
            Console.WriteLine($"  Trades:         {entry.TradesCount}");

            if (entry.BenchmarkComparisons.Count == 0)
            {
                continue;
            }

            Console.WriteLine("  --- Benchmark ---");
            foreach (var comparison in entry.BenchmarkComparisons)
            {
                Console.WriteLine($"  {comparison.Symbol}");
                Console.WriteLine($"    Benchmark Return: {comparison.BenchmarkReturnPercent:N2}%");
                Console.WriteLine($"    Relative Return:  {comparison.RelativeReturnPercent:N2}%");
                Console.WriteLine($"    Alpha:            {comparison.AlphaPercent:N2}%");
                Console.WriteLine($"    Outcome:          {comparison.Outcome}");
            }
        }
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
        Console.WriteLine($"Strategy: {result.StrategyId}");
        Console.WriteLine($"Start Capital:  {result.StartCapital:N2}");
        Console.WriteLine($"Final Capital:  {result.FinalCapital:N2}");
        Console.WriteLine($"Profit:         {result.Profit:N2}");
        Console.WriteLine($"Return:         {result.ReturnPercent:N2}%");
        Console.WriteLine($"Trades:         {result.TradesCount}");
        Console.WriteLine($"Rebalances:     {result.RebalancesCount}");

        Console.WriteLine();
        Console.WriteLine("--- Risk ---");
        Console.WriteLine($"Max Drawdown:   {result.MaxDrawdownPercent:N2}%");
        Console.WriteLine($"DD Duration:    {result.DrawdownDurationDays} day(s)");
        Console.WriteLine(result.RecoveryTimeDays.HasValue
            ? $"Recovery Time:  {result.RecoveryTimeDays.Value} day(s)"
            : "Recovery Time:  N/A (not recovered)");

        if (result.BenchmarkComparisons.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("--- Benchmark ---");

        foreach (var comparison in result.BenchmarkComparisons)
        {
            Console.WriteLine($"{comparison.Symbol}");
            Console.WriteLine($"  Strategy Return:  {comparison.StrategyReturnPercent:N2}%");
            Console.WriteLine($"  Benchmark Return: {comparison.BenchmarkReturnPercent:N2}%");
            Console.WriteLine($"  Relative Return:  {comparison.RelativeReturnPercent:N2}%");
            Console.WriteLine($"  Alpha:            {comparison.AlphaPercent:N2}%");
            Console.WriteLine($"  Outcome:          {comparison.Outcome}");
        }
    }
}
