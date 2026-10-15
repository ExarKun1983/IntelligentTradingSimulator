using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyTuningReportWorkflow(IStrategyTuningAnalysisService analysisService)
{
    public async Task<StrategyTuningReport> RunAsync(
        string inputPath, string? outputPath, CancellationToken cancellationToken = default)
    {
        if (outputPath is not null && string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Tuning output must not overwrite the input request.", nameof(outputPath));

        var request = await StrategyTuningJsonFile.ReadAsync(inputPath, cancellationToken);
        var report = analysisService.Analyze(request);
        Console.WriteLine($"Strategy tuning research ({report.Method}) — decision support only");
        foreach (var analysis in report.Analyses.OrderBy(item => item.Metrics.Rank ?? int.MaxValue)
                     .ThenBy(item => item.StrategyId, StringComparer.Ordinal).ThenBy(item => item.BacktestRunId))
        {
            Console.WriteLine($"Run {analysis.BacktestRunId} / {analysis.StrategyId}: " +
                $"return rank {analysis.Metrics.Rank?.ToString() ?? "unranked"}; " +
                $"high-performing={analysis.IsHighPerforming}; high-risk={analysis.IsHighRisk}; promising={analysis.IsPromising}");
            Console.WriteLine($"  Historical parameters: {analysis.ConfigurationJson ?? "unavailable"}");
            foreach (var text in analysis.Strengths)
                Console.WriteLine($"  Strength: {text}");
            foreach (var text in analysis.Weaknesses)
                Console.WriteLine($"  Weakness: {text}");
            foreach (var text in analysis.Limitations)
                Console.WriteLine($"  Limitation: {text}");
        }
        foreach (var text in report.Limitations)
            Console.WriteLine($"Limitation: {text}");
        if (outputPath is not null)
        {
            await StrategyTuningJsonFile.ExportAsync(outputPath, report, cancellationToken);
            Console.WriteLine($"Strategy tuning research exported to {outputPath}");
        }
        return report;
    }
}
