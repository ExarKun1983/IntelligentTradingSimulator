using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyComparisonReportWorkflow(
    IStrategyComparisonReportService reportService,
    ConsoleReporter reporter,
    StrategyComparisonCsvExporter exporter,
    IWorkflowProgress? progress = null)
{
    private readonly IWorkflowProgress _progress = progress ?? NullWorkflowProgress.Instance;

    public async Task RunAsync(
        IReadOnlyList<BacktestSummaryReportResult> summaries,
        StrategyComparisonSortMetric? sortBy,
        string? benchmarkSymbol,
        string? csvFilePath,
        CancellationToken cancellationToken = default)
    {
        StrategyComparisonReport report;
        using (_progress.BeginOperation("Generating strategy comparison"))
            report = reportService.Build(summaries, sortBy, benchmarkSymbol);
        reporter.PrintVariantComparisonReport(report);

        if (csvFilePath is not null)
        {
            try
            {
                using (_progress.BeginOperation("Exporting strategy comparison"))
                    await exporter.ExportAsync(csvFilePath, report, cancellationToken);
            }
            catch (Exception)
            {
                _progress.Report("Strategy comparison export failed.", force: true);
                throw;
            }
            _progress.Report("Strategy comparison CSV export complete", force: true);
            Console.WriteLine($"Strategy comparison exported to {csvFilePath}");
        }
    }
}
