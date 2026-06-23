using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;

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
                ProcessedDays = snapshots.Count,
                BestDayReturnPercent = snapshots.Max(x => x.ReturnPercent),
                WorstDayReturnPercent = snapshots.Min(x => x.ReturnPercent)
            }
        };
    }
}
