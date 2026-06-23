using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Reporting;

public sealed class BacktestSummaryReportService
{
    private readonly IBacktestRunRepository _runRepository;
    private readonly IBacktestSnapshotRepository _snapshotRepository;

    public BacktestSummaryReportService(
        IBacktestRunRepository runRepository,
        IBacktestSnapshotRepository snapshotRepository)
    {
        _runRepository = runRepository;
        _snapshotRepository = snapshotRepository;
    }

    public async Task<BacktestSummaryReportResult> GenerateAsync(int runId)
    {
        var run = await _runRepository.GetByIdAsync(runId);

        if (run is null)
        {
            return new BacktestSummaryReportResult
            {
                RunId = runId,
                ErrorMessage = $"Backtest run {runId} was not found."
            };
        }

        if (run.Status != BacktestRunStatus.Completed)
        {
            return new BacktestSummaryReportResult
            {
                RunId = runId,
                ErrorMessage = $"Backtest run {runId} is not completed."
            };
        }

        var snapshots = await _snapshotRepository.GetByRunIdAsync(runId);

        if (snapshots.Count == 0)
        {
            return new BacktestSummaryReportResult
            {
                RunId = runId,
                ErrorMessage = $"No backtest snapshots were found for run {runId}."
            };
        }

        var (tradesCount, rebalancesCount) =
            CalculateInferredCounts(run.InitialCapital, snapshots);

        return new BacktestSummaryReportResult
        {
            RunId = runId,
            Report = new BacktestSummaryReport
            {
                RunId = run.Id,
                Name = run.Name,
                StartDate = run.StartDate,
                EndDate = run.EndDate,
                InitialCapital = run.InitialCapital,
                FinalCapital = run.FinalCapital,
                TotalProfit = run.TotalProfit,
                TotalReturnPercent = run.TotalReturnPercent,
                TradesCount = tradesCount,
                RebalancesCount = rebalancesCount,
                ProcessedDays = snapshots.Count,
                BestDayReturnPercent = snapshots.Max(x => x.ReturnPercent),
                WorstDayReturnPercent = snapshots.Min(x => x.ReturnPercent)
            }
        };
    }

    private static (int TradesCount, int RebalancesCount) CalculateInferredCounts(
        decimal initialCapital,
        IReadOnlyList<BacktestSnapshot> snapshots)
    {
        const decimal cashChangeThreshold = 0.0001m;

        var previousCash = initialCapital;
        var inferredRebalances = 0;
        var inferredTrades = 0;

        foreach (var snapshot in snapshots)
        {
            if (Math.Abs(snapshot.Cash - previousCash) > cashChangeThreshold)
            {
                // Backtest snapshots do not persist per-trade/per-rebalance events.
                // We infer a reliable minimum: any day with cash movement must have
                // executed at least one rebalance and at least one trade.
                inferredRebalances++;
                inferredTrades++;
            }

            previousCash = snapshot.Cash;
        }

        return (inferredTrades, inferredRebalances);
    }
}
