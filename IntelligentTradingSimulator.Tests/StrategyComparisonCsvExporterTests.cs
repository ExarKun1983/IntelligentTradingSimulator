using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyComparisonCsvExporterTests
{
    private const string Header =
        "StrategyId,Rank,IsSuccess,ErrorMessage,StartCapital,FinalCapital,ReturnPercent," +
        "MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount";

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task WriteAsync_UsesReportSchemaAndInvariantValues(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            using var writer = new StringWriter();

            await new StrategyComparisonCsvExporter().WriteAsync(writer, new()
            {
                Entries = [CreateEntry()]
            });

            Assert.Equal(Header + "\r\n" +
                "default,1,True,,1000.25,1105.27625,10.5,2.1234567890123456789012345678,3,5,7\r\n",
                writer.ToString());
            var properties = typeof(StrategyComparisonReportEntry).GetProperties()
                .Where(p => p.Name is not nameof(StrategyComparisonReportEntry.BenchmarkComparisons)
                    and not nameof(StrategyComparisonReportEntry.Status))
                .Select(p => p.Name).OrderBy(name => name).ToArray();
            Assert.Equal(properties, Header.Split(',').OrderBy(name => name).ToArray());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task WriteAsync_OneRowPerVariantWithAlignedBenchmarkColumnsAndReportOrder()
    {
        var first = CreateEntry();
        first.StrategyId = "z-first";
        first.BenchmarkComparisons =
        [
            new() { Symbol = "SPY", StrategyReturnPercent = 10.5m, BenchmarkReturnPercent = 4.125m,
                RelativeReturnPercent = 6.375m, AlphaPercent = 6.375m, Outcome = BacktestBenchmarkComparisonOutcome.Outperform },
            new() { Symbol = "QQQ", StrategyReturnPercent = 10.5m, BenchmarkReturnPercent = 12m,
                RelativeReturnPercent = -1.5m, AlphaPercent = -1.5m, Outcome = BacktestBenchmarkComparisonOutcome.Underperform }
        ];
        var second = CreateEntry();
        second.StrategyId = "a-second";
        second.Rank = null;
        second.RecoveryTimeDays = null;
        second.BenchmarkComparisons = [new() { Symbol = "SPY" }];
        using var writer = new StringWriter();

        await new StrategyComparisonCsvExporter().WriteAsync(writer, new() { Entries = [first, second] });

        var lines = writer.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(Header +
            ",QQQ.StrategyReturnPercent,QQQ.BenchmarkReturnPercent,QQQ.RelativeReturnPercent,QQQ.AlphaPercent,QQQ.Outcome" +
            ",SPY.StrategyReturnPercent,SPY.BenchmarkReturnPercent,SPY.RelativeReturnPercent,SPY.AlphaPercent,SPY.Outcome",
            lines[0]);
        Assert.StartsWith("z-first,1,True,", lines[1]);
        Assert.EndsWith(",10.5,12,-1.5,-1.5,Underperform,10.5,4.125,6.375,6.375,Outperform", lines[1]);
        Assert.StartsWith("a-second,,True,", lines[2]);
        Assert.EndsWith(",3,,7,,,,,,0,0,0,0,Outperform", lines[2]);
        Assert.All(lines, line => Assert.Equal(21, line.Split(',').Length));
    }

    [Fact]
    public async Task WriteAsync_IncludesFailedVariantsWithoutFabricatedMetrics()
    {
        using var writer = new StringWriter();
        await new StrategyComparisonCsvExporter().WriteAsync(writer, new()
        {
            Entries =
            [
                CreateEntry(),
                new() { StrategyId = "failed", ErrorMessage = "Unavailable", ReturnPercent = 123m }
            ]
        });

        Assert.EndsWith("failed,,False,Unavailable,,,,,,,\r\n", writer.ToString());
    }

    [Fact]
    public async Task WriteAsync_EscapesTextAndBenchmarkHeaders()
    {
        var entry = CreateEntry();
        entry.StrategyId = "strategy, \"quoted\"\r\nnext";
        entry.ErrorMessage = "error,\n\"detail\"";
        entry.BenchmarkComparisons = [new() { Symbol = "B,\"Q" }];
        using var writer = new StringWriter();

        await new StrategyComparisonCsvExporter().WriteAsync(writer, new() { Entries = [entry] });

        Assert.Contains("\"strategy, \"\"quoted\"\"\r\nnext\",1,True,\"error,\n\"\"detail\"\"\",", writer.ToString());
        Assert.Contains("\"B,\"\"Q.AlphaPercent\"", writer.ToString());
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
    public async Task WriteAsync_NeutralizesFormulasInAllTextFields(string value)
    {
        var entry = CreateEntry();
        entry.StrategyId = value;
        entry.ErrorMessage = value;
        entry.ReturnPercent = -10.5m;
        entry.BenchmarkComparisons = [new() { Symbol = value }];
        using var writer = new StringWriter();

        await new StrategyComparisonCsvExporter().WriteAsync(writer, new() { Entries = [entry] });

        static string Quote(string text) => text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        Assert.Contains($"{Quote("'" + value)},1,True,{Quote("'" + value)},", writer.ToString());
        Assert.Contains(Quote("'" + value + ".AlphaPercent"), writer.ToString());
        Assert.Contains(",-10.5,", writer.ToString());
    }

    [Fact]
    public async Task WriteAsync_EmptyReportWritesOnlyHeader()
    {
        using var writer = new StringWriter();
        await new StrategyComparisonCsvExporter().WriteAsync(writer, new());
        Assert.Equal(Header + "\r\n", writer.ToString());
    }

    [Fact]
    public async Task ExportAsync_WritesUtf8WithoutBomAndOverwritesExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comparison-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "old content");
            var entry = CreateEntry();
            entry.StrategyId = "Résumé";
            await new StrategyComparisonCsvExporter().ExportAsync(path, new() { Entries = [entry] });
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
            var content = Encoding.UTF8.GetString(bytes);
            Assert.StartsWith(Header + "\r\nRésumé,", content);
            Assert.DoesNotContain("old content", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportAsync_CancellationDoesNotTouchExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comparison-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "existing");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new StrategyComparisonCsvExporter().ExportAsync(path, new(), new CancellationToken(true)));
            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static StrategyComparisonReportEntry CreateEntry() => new()
    {
        StrategyId = "default",
        Rank = 1,
        IsSuccess = true,
        Status = IntelligentTradingSimulator.Core.Enums.BacktestRunStatus.Completed,
        StartCapital = 1000.25m,
        FinalCapital = 1105.27625m,
        ReturnPercent = 10.5m,
        MaxDrawdownPercent = 2.1234567890123456789012345678m,
        DrawdownDurationDays = 3,
        RecoveryTimeDays = 5,
        TradesCount = 7
    };
}
