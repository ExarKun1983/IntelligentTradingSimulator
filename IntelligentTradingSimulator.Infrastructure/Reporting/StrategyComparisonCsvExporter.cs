using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.Reporting;

public sealed class StrategyComparisonCsvExporter
{
    private const string Header =
        "StrategyId,Rank,IsSuccess,ErrorMessage,StartDate,EndDate,HistoricalStartDate,StartCapital,FinalCapital,ReturnPercent," +
        "MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount";

    private static readonly string[] BenchmarkFields =
    [
        nameof(BacktestBenchmarkComparison.StrategyReturnPercent),
        nameof(BacktestBenchmarkComparison.BenchmarkReturnPercent),
        nameof(BacktestBenchmarkComparison.RelativeReturnPercent),
        nameof(BacktestBenchmarkComparison.AlphaPercent),
        nameof(BacktestBenchmarkComparison.Outcome)
    ];

    public async Task ExportAsync(
        string filePath,
        StrategyComparisonReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
        await WriteAsync(writer, report, cancellationToken);
    }

    public async Task WriteAsync(
        TextWriter writer,
        StrategyComparisonReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        var symbols = report.Entries.SelectMany(entry => entry.BenchmarkComparisons)
            .Select(benchmark => benchmark.Symbol)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(symbol => symbol, StringComparer.Ordinal)
            .ToArray();
        var benchmarkHeaders = symbols.SelectMany(symbol =>
            BenchmarkFields.Select(field => EscapeText($"{symbol}.{field}")));
        await writer.WriteAsync(
            (string.Join(",", new[] { Header }.Concat(benchmarkHeaders)) + "\r\n").AsMemory(),
            cancellationToken);

        foreach (var entry in report.Entries)
        {
            var fields = new List<string>
            {
                EscapeText(entry.StrategyId),
                Format(entry.Rank),
                entry.IsSuccess.ToString(),
                EscapeText(entry.ErrorMessage ?? ""),
                entry.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                entry.HistoricalStartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                entry.IsSuccess ? Format(entry.StartCapital) : "",
                entry.IsSuccess ? Format(entry.FinalCapital) : "",
                entry.IsSuccess ? Format(entry.ReturnPercent) : "",
                entry.IsSuccess ? Format(entry.MaxDrawdownPercent) : "",
                entry.IsSuccess ? Format(entry.DrawdownDurationDays) : "",
                entry.IsSuccess ? Format(entry.RecoveryTimeDays) : "",
                entry.IsSuccess ? Format(entry.TradesCount) : ""
            };

            foreach (var symbol in symbols)
            {
                var benchmark = entry.IsSuccess
                    ? entry.BenchmarkComparisons.FirstOrDefault(b => b.Symbol == symbol)
                    : null;
                fields.AddRange(benchmark is null
                    ? ["", "", "", "", ""]
                    : new[]
                    {
                        Format(benchmark.StrategyReturnPercent),
                        Format(benchmark.BenchmarkReturnPercent),
                        Format(benchmark.RelativeReturnPercent),
                        Format(benchmark.AlphaPercent),
                        benchmark.Outcome.ToString()
                    });
            }

            await writer.WriteAsync(
                (string.Join(",", fields) + "\r\n").AsMemory(), cancellationToken);
        }
    }

    private static string Format(decimal? value) =>
        value?.ToString("G29", CultureInfo.InvariantCulture) ?? "";

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
