using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestReportWorkflow(
    IBacktestRunRepository runRepository,
    IBacktestSnapshotRepository snapshotRepository,
    BacktestSnapshotCsvExporter snapshotExporter,
    IBacktestSummaryReportService reportService,
    ConsoleReporter reporter,
    BacktestSummaryCsvExporter summaryExporter,
    IWorkflowProgress? progress = null)
{
    private readonly IWorkflowProgress _progress = progress ?? NullWorkflowProgress.Instance;

    public async Task<bool> RunAsync(int runId, string? csvFilePath, string? snapshotCsvFilePath)
    {
        _progress.Context = _progress.Context with
        {
            Workflow = "backtest-report",
            RunId = runId,
            Date = null,
            CompletedDates = null,
            TotalDates = null,
            CompletedSymbols = null,
            TotalSymbols = null
        };
        var exportsSucceeded = true;
        try
        {
            if (snapshotCsvFilePath is not null)
            {
                bool exists;
                using (_progress.BeginOperation("Loading backtest run"))
                    exists = await runRepository.ExistsAsync(runId);
                if (!exists)
                {
                    exportsSucceeded = false;
                    _progress.Report($"Backtest run {runId} was not found.", force: true);
                    Console.WriteLine($"Backtest run {runId} was not found.");
                }
                else
                {
                    IReadOnlyList<BacktestSnapshot> snapshots;
                    using (_progress.BeginOperation("Loading backtest snapshots"))
                        snapshots = await snapshotRepository.GetByRunIdAsync(runId);
                    using (_progress.BeginOperation("Exporting backtest snapshots"))
                        await snapshotExporter.ExportAsync(snapshotCsvFilePath, snapshots);
                    _progress.Report(
                        "Snapshot export may contain partial data for running or failed backtest runs.",
                        force: true);
                    _progress.Report("Snapshot CSV export complete", force: true);
                    Console.WriteLine($"Backtest snapshots exported to {snapshotCsvFilePath}");
                }

                if (csvFilePath is null)
                    return exportsSucceeded;
            }

            BacktestSummaryReportResult result;
            using (_progress.BeginOperation("Loading report database data and generating summary"))
                result = await reportService.GenerateAsync(runId);
            _progress.Context = _progress.Context with { StrategyId = result.StrategyId };
            reporter.PrintBacktestSummary(result);
            if (!result.IsSuccess)
                _progress.Report($"Backtest report failed: {result.ErrorMessage}", force: true);
            if (csvFilePath is not null && result.IsSuccess)
            {
                if (result.Status != BacktestRunStatus.Completed)
                {
                    _progress.Report("CSV export requires a completed backtest run.", force: true);
                    Console.WriteLine("CSV export requires a completed backtest run.");
                    return false;
                }

                using (_progress.BeginOperation("Exporting backtest summary"))
                    await summaryExporter.ExportAsync(csvFilePath, [result]);
                _progress.Report("Summary CSV export complete", force: true);
                Console.WriteLine($"Backtest summary exported to {csvFilePath}");
            }
            return exportsSucceeded && result.IsSuccess;
        }
        catch (Exception)
        {
            _progress.Report($"Backtest report failed for run {runId}.", force: true);
            throw;
        }
    }
}
