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
                    Outcome = GetComparisonOutcome(run.TotalReturnPercent, b.ReturnPercent)
                })
                .ToList()
            : new List<BacktestBenchmarkComparison>();

        return new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = run.Id,
            RunName = run.Name,
            StartCapital = run.InitialCapital,
            FinalCapital = run.FinalCapital,
            Profit = run.TotalProfit,
            ReturnPercent = run.TotalReturnPercent,
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
}
