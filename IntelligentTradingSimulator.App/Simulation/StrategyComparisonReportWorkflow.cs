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
    StrategyComparisonCsvExporter exporter)
{
    public async Task RunAsync(
        IReadOnlyList<BacktestSummaryReportResult> summaries,
        StrategyComparisonSortMetric? sortBy,
        string? benchmarkSymbol,
        string? csvFilePath,
        CancellationToken cancellationToken = default)
    {
        var report = reportService.Build(summaries, sortBy, benchmarkSymbol);
        reporter.PrintVariantComparisonReport(report);

        if (csvFilePath is not null)
        {
            await exporter.ExportAsync(csvFilePath, report, cancellationToken);
            Console.WriteLine($"Strategy comparison exported to {csvFilePath}");
        }
    }
}
