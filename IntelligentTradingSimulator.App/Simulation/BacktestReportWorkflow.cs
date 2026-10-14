using System;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestReportWorkflow(
    IBacktestRunRepository runRepository,
    IBacktestSnapshotRepository snapshotRepository,
    BacktestSnapshotCsvExporter snapshotExporter,
    IBacktestSummaryReportService reportService,
    ConsoleReporter reporter,
    BacktestSummaryCsvExporter summaryExporter)
{
    public async Task RunAsync(int runId, string? csvFilePath, string? snapshotCsvFilePath)
    {
        if (snapshotCsvFilePath is not null)
        {
            if (await runRepository.GetByIdAsync(runId) is null)
            {
                Console.WriteLine($"Backtest run {runId} was not found.");
            }
            else
            {
                var snapshots = await snapshotRepository.GetByRunIdAsync(runId);
                await snapshotExporter.ExportAsync(snapshotCsvFilePath, snapshots);
                Console.WriteLine($"Backtest snapshots exported to {snapshotCsvFilePath}");
            }

            if (csvFilePath is null)
                return;
        }

        var result = await reportService.GenerateAsync(runId);
        reporter.PrintBacktestSummary(result);
        if (csvFilePath is not null && result.IsSuccess)
        {
            if (result.Status != BacktestRunStatus.Completed)
            {
                Console.WriteLine("CSV export requires a completed backtest run.");
                return;
            }

            await summaryExporter.ExportAsync(csvFilePath, [result]);
            Console.WriteLine($"Backtest summary exported to {csvFilePath}");
        }
    }
}
