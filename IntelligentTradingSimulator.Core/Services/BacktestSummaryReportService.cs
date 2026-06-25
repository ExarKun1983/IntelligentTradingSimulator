using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class BacktestSummaryReportService : IBacktestSummaryReportService
{
    private readonly IBacktestRunRepository _runRepository;
    private readonly IBacktestSnapshotRepository _snapshotRepository;
    private readonly IBacktestBenchmarkService _benchmarkService;

    public BacktestSummaryReportService(
        IBacktestRunRepository runRepository,
        IBacktestSnapshotRepository snapshotRepository,
        IBacktestBenchmarkService benchmarkService)
    {
        _runRepository = runRepository;
        _snapshotRepository = snapshotRepository;
        _benchmarkService = benchmarkService;
    }

    public async Task<BacktestSummaryReportResult> GenerateAsync(int backtestRunId)
    {
        var run = await _runRepository.GetByIdAsync(backtestRunId);

        if (run is null)
        {
            return new BacktestSummaryReportResult
            {
                IsSuccess = false,
                ErrorMessage = $"Backtest run {backtestRunId} not found."
            };
        }

        var snapshots = await _snapshotRepository.GetByRunIdAsync(backtestRunId);

        if (snapshots.Count == 0)
        {
            return new BacktestSummaryReportResult
            {
                IsSuccess = false,
                ErrorMessage = $"No snapshots found for backtest run {backtestRunId}."
            };
        }

        var (maxDrawdownPercent, drawdownDurationDays, recoveryTimeDays) = GetDrawdownMetrics(snapshots);
        var tradesCount = snapshots.Sum(s => s.TradesExecuted);
        var rebalancesCount = snapshots.Count(s => s.RebalanceExecuted);
        var benchmarkResult = await _benchmarkService.GetForRunAsync(backtestRunId);
        var benchmarkComparisons = benchmarkResult.IsSuccess
            ? benchmarkResult.BenchmarkResults
                .Select(b => new BacktestBenchmarkComparison
                {
                    Symbol = b.Symbol,
                    StrategyReturnPercent = run.TotalReturnPercent,
                    BenchmarkReturnPercent = b.ReturnPercent,
                    RelativeReturnPercent = GetRelativeReturnPercent(run.TotalReturnPercent, b.ReturnPercent),
                    AlphaPercent = GetAlphaPercent(run.TotalReturnPercent, b.ReturnPercent),
                    Outcome = GetComparisonOutcome(run.TotalReturnPercent, b.ReturnPercent)
                })
                .ToList()
            : new List<BacktestBenchmarkComparison>();

        return new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = run.Id,
            RunName = run.Name,
            StrategyId = run.StrategyId,
            StartCapital = run.InitialCapital,
            FinalCapital = run.FinalCapital,
            Profit = run.TotalProfit,
            ReturnPercent = run.TotalReturnPercent,
            MaxDrawdownPercent = maxDrawdownPercent,
            DrawdownDurationDays = drawdownDurationDays,
            RecoveryTimeDays = recoveryTimeDays,
            TradesCount = tradesCount,
            RebalancesCount = rebalancesCount,
            BenchmarkComparisons = benchmarkComparisons
        };
    }

    private static BacktestBenchmarkComparisonOutcome GetComparisonOutcome(
        decimal strategyReturnPercent,
        decimal benchmarkReturnPercent)
    {
        if (strategyReturnPercent > benchmarkReturnPercent)
        {
            return BacktestBenchmarkComparisonOutcome.Outperform;
        }

        if (strategyReturnPercent < benchmarkReturnPercent)
        {
            return BacktestBenchmarkComparisonOutcome.Underperform;
        }

        return BacktestBenchmarkComparisonOutcome.Match;
    }

    private static decimal GetRelativeReturnPercent(
        decimal strategyReturnPercent,
        decimal benchmarkReturnPercent)
    {
        return strategyReturnPercent - benchmarkReturnPercent;
    }

    private static decimal GetAlphaPercent(
        decimal strategyReturnPercent,
        decimal benchmarkReturnPercent)
    {
        return GetRelativeReturnPercent(strategyReturnPercent, benchmarkReturnPercent);
    }

    private static (decimal maxDrawdownPercent, int drawdownDurationDays, int? recoveryTimeDays)
        GetDrawdownMetrics(IReadOnlyList<BacktestSnapshot> snapshots)
    {
        if (snapshots.Count == 0)
        {
            return (0m, 0, null);
        }

        decimal peak = snapshots[0].PortfolioValue;
        DateTime peakDate = snapshots[0].Date;

        decimal maxDrawdown = 0m;
        decimal maxDrawdownPeakValue = snapshots[0].PortfolioValue;
        DateTime maxDrawdownPeakDate = snapshots[0].Date;
        DateTime maxDrawdownTroughDate = snapshots[0].Date;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.PortfolioValue > peak)
            {
                peak = snapshot.PortfolioValue;
                peakDate = snapshot.Date;
            }

            if (peak <= 0m)
            {
                continue;
            }

            var drawdown = (peak - snapshot.PortfolioValue) / peak * 100m;

            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
                maxDrawdownPeakValue = peak;
                maxDrawdownPeakDate = peakDate;
                maxDrawdownTroughDate = snapshot.Date;
            }
        }

        if (maxDrawdown == 0m)
        {
            return (0m, 0, null);
        }

        var drawdownDurationDays =
            (int)(maxDrawdownTroughDate - maxDrawdownPeakDate).TotalDays;

        int? recoveryTimeDays = null;
        var troughIndex = snapshots
            .Select((s, i) => (snapshot: s, index: i))
            .First(x => x.snapshot.Date == maxDrawdownTroughDate)
            .index;

        for (var i = troughIndex + 1; i < snapshots.Count; i++)
        {
            if (snapshots[i].PortfolioValue >= maxDrawdownPeakValue)
            {
                recoveryTimeDays =
                    (int)(snapshots[i].Date - maxDrawdownTroughDate).TotalDays;
                break;
            }
        }

        return (maxDrawdown, drawdownDurationDays, recoveryTimeDays);
    }
}
