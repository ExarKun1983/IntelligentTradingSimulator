using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.Reporting;

public sealed class BacktestSummaryCsvExporter
{
    private const string Header =
        "BacktestRunId,BacktestName,StrategyId,StartDate,EndDate,InitialCapital,FinalCapital,Profit,ReturnPercent," +
        "MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount,RebalancesCount," +
        "BenchmarkSymbol,BenchmarkReturnPercent,RelativeReturnPercent,AlphaPercent,BenchmarkOutcome";

    public async Task ExportAsync(
        string filePath,
        IEnumerable<BacktestSummaryReportResult> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var results = Validate(summaries);
        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
        await WriteAsync(writer, results, cancellationToken);
    }

    public async Task WriteAsync(
        TextWriter writer,
        IEnumerable<BacktestSummaryReportResult> summaries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var results = Validate(summaries);
        await writer.WriteAsync((Header + "\r\n").AsMemory(), cancellationToken);

        foreach (var summary in results)
        {
            if (summary.BenchmarkComparisons.Count == 0)
            {
                await WriteRowAsync(writer, summary, null, cancellationToken);
                continue;
            }

            foreach (var benchmark in summary.BenchmarkComparisons.OrderBy(b => b.Symbol, StringComparer.Ordinal))
            {
                await WriteRowAsync(writer, summary, benchmark, cancellationToken);
            }
        }
    }

    private static BacktestSummaryReportResult[] Validate(IEnumerable<BacktestSummaryReportResult> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        var results = summaries.ToArray();
        if (results.Any(s => s is null || !s.IsSuccess || s.Status != BacktestRunStatus.Completed))
        {
            throw new ArgumentException("Only successful summaries of completed backtest runs can be exported.", nameof(summaries));
        }
        return results;
    }

    private static Task WriteRowAsync(
        TextWriter writer,
        BacktestSummaryReportResult summary,
        BacktestBenchmarkComparison? benchmark,
        CancellationToken cancellationToken)
    {
        string[] fields =
        [
            Format(summary.BacktestRunId),
            EscapeText(summary.RunName),
            EscapeText(summary.StrategyId),
            summary.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            summary.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Format(summary.StartCapital),
            Format(summary.FinalCapital),
            Format(summary.Profit),
            Format(summary.ReturnPercent),
            Format(summary.MaxDrawdownPercent),
            Format(summary.DrawdownDurationDays),
            summary.RecoveryTimeDays.HasValue ? Format(summary.RecoveryTimeDays.Value) : "",
            Format(summary.TradesCount),
            Format(summary.RebalancesCount),
            benchmark is null ? "" : EscapeText(benchmark.Symbol),
            benchmark is null ? "" : Format(benchmark.BenchmarkReturnPercent),
            benchmark is null ? "" : Format(benchmark.RelativeReturnPercent),
            benchmark is null ? "" : Format(benchmark.AlphaPercent),
            benchmark?.Outcome.ToString() ?? ""
        ];

        return writer.WriteAsync((string.Join(",", fields) + "\r\n").AsMemory(), cancellationToken);
    }

    private static string Format(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);

    private static string EscapeText(string value)
    {
        // CSV quoting alone does not prevent spreadsheet formula execution.
        var trimmed = value.TrimStart();
        if ((trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) ||
            (value.Length > 0 && "\t\r\n".Contains(value[0])))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
