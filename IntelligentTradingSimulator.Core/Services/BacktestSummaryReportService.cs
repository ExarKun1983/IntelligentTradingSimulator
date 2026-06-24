using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class BacktestSummaryReportService : IBacktestSummaryReportService
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
            RebalancesCount = rebalancesCount
        };
    }
}
