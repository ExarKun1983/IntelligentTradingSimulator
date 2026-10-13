using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSummaryCsvExporterTests
{
    private const string Header =
        "BacktestRunId,BacktestName,StrategyName,StartDate,EndDate,InitialCapital,FinalCapital,Profit,ReturnPercent," +
        "MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount,RebalancesCount," +
        "BenchmarkSymbol,BenchmarkReturnPercent,RelativeReturnPercent,AlphaPercent,BenchmarkOutcome\r\n";

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task WriteAsync_UsesStableSchemaAndInvariantValues(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            var summary = CreateSummary();
            summary.BenchmarkComparisons =
            [
                new BacktestBenchmarkComparison
                {
                    Symbol = "SPY",
                    BenchmarkReturnPercent = 4.125m,
                    RelativeReturnPercent = 6.375m,
                    AlphaPercent = 6.375m,
                    Outcome = BacktestBenchmarkComparisonOutcome.Outperform
                }
            ];

            using var writer = new StringWriter();
            await new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]);

            Assert.Equal(Header +
                "42,Quarterly run,default,2024-01-01,2024-03-31,1000.25,1105.27625,105.02625,10.5," +
                "2.1234567890123456789012345678,3,5,7,2,SPY,4.125,6.375,6.375,Outperform\r\n",
                writer.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task WriteAsync_EscapesCommasQuotesAndLineBreaks()
    {
        var summary = CreateSummary();
        summary.RunName = "Run, \"quoted\"\r\nnext";
        summary.StrategyId = "strategy,\n\"name\"";
        using var writer = new StringWriter();

        await new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]);

        Assert.Contains("42,\"Run, \"\"quoted\"\"\r\nnext\",\"strategy,\n\"\"name\"\"\",2024-01-01", writer.ToString());
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1)")]
    [InlineData("  =1+1")]
    [InlineData("\tname")]
    [InlineData("\rname")]
    [InlineData("\nname")]
    public async Task WriteAsync_NeutralizesSpreadsheetFormulasInText(string value)
    {
        var summary = CreateSummary();
        summary.RunName = value;
        summary.StrategyId = value;
        summary.Profit = -100.25m;
        summary.ReturnPercent = -10.5m;
        summary.BenchmarkComparisons = [new BacktestBenchmarkComparison { Symbol = value }];
        using var writer = new StringWriter();

        await new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]);

        var safeValue = "'" + value;
        if (safeValue.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            safeValue = "\"" + safeValue.Replace("\"", "\"\"") + "\"";
        Assert.Contains($"42,{safeValue},{safeValue},2024-01-01", writer.ToString());
        Assert.Contains($",2,{safeValue},0,0,0,Outperform\r\n", writer.ToString());
        Assert.Contains(",-100.25,-10.5,", writer.ToString());
    }

    [Fact]
    public async Task WriteAsync_LeavesUnavailableMetricsEmpty()
    {
        var summary = CreateSummary();
        summary.RecoveryTimeDays = null;
        using var writer = new StringWriter();

        await new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]);

        Assert.Equal(Header +
            "42,Quarterly run,default,2024-01-01,2024-03-31,1000.25,1105.27625,105.02625,10.5," +
            "2.1234567890123456789012345678,3,,7,2,,,,,\r\n", writer.ToString());
    }

    [Fact]
    public async Task WriteAsync_SupportsMultipleRunsAndOrdersBenchmarkRows()
    {
        var first = CreateSummary();
        first.BenchmarkComparisons =
        [
            new BacktestBenchmarkComparison { Symbol = "SPY", BenchmarkReturnPercent = 3m },
            new BacktestBenchmarkComparison { Symbol = "QQQ", BenchmarkReturnPercent = -2m }
        ];
        var second = CreateSummary();
        second.BacktestRunId = 43;
        using var writer = new StringWriter();

        await new BacktestSummaryCsvExporter().WriteAsync(writer, [first, second]);

        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.EndsWith(",QQQ,-2,0,0,Outperform", lines[1]);
        Assert.EndsWith(",SPY,3,0,0,Outperform", lines[2]);
        Assert.StartsWith("43,", lines[3]);
        Assert.All(lines, line => Assert.Equal(19, line.Split(',').Length));
    }

    [Fact]
    public async Task WriteAsync_EmptyCollectionWritesOnlyHeader()
    {
        using var writer = new StringWriter();
        await new BacktestSummaryCsvExporter().WriteAsync(writer, []);
        Assert.Equal(Header, writer.ToString());
    }

    [Fact]
    public async Task ExportAsync_WritesUtf8WithoutBomAndOverwritesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backtest-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "old content");
            var summary = CreateSummary();
            summary.RunName = "Résumé";

            await new BacktestSummaryCsvExporter().ExportAsync(path, [summary]);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
            var content = Encoding.UTF8.GetString(bytes);
            Assert.StartsWith(Header, content);
            Assert.Contains("42,Résumé,default,", content);
            Assert.DoesNotContain("old content", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportAsync_RejectsFailedSummaryBeforeTouchingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backtest-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "existing");
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new BacktestSummaryCsvExporter().ExportAsync(path, [CreateSummary(), new() { IsSuccess = false }]));
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportAsync_RejectsIncompleteRunsWithoutCreatingFile()
    {
        var exporter = new BacktestSummaryCsvExporter();
        foreach (var status in new[] { BacktestRunStatus.Running, BacktestRunStatus.Failed })
        {
            var summary = CreateSummary();
            summary.Status = status;
            var path = Path.Combine(Path.GetTempPath(), $"backtest-{Guid.NewGuid():N}.csv");

            await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(path, [summary]));

            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public async Task ExportAsync_CancellationDoesNotCreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"backtest-{Guid.NewGuid():N}.csv");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BacktestSummaryCsvExporter().ExportAsync(path, [CreateSummary()], new CancellationToken(true)));
        Assert.False(File.Exists(path));
    }

    private static BacktestSummaryReportResult CreateSummary() => new()
    {
        IsSuccess = true,
        Status = BacktestRunStatus.Completed,
        BacktestRunId = 42,
        RunName = "Quarterly run",
        StrategyId = "default",
        StartDate = new DateTime(2024, 1, 1),
        EndDate = new DateTime(2024, 3, 31),
        StartCapital = 1000.25m,
        FinalCapital = 1105.27625m,
        Profit = 105.02625m,
        ReturnPercent = 10.5m,
        MaxDrawdownPercent = 2.1234567890123456789012345678m,
        DrawdownDurationDays = 3,
        RecoveryTimeDays = 5,
        TradesCount = 7,
        RebalancesCount = 2
    };
}
