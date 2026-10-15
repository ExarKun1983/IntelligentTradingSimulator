using System.Text.Json;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Reporting;

namespace IntelligentTradingSimulator.Tests;

public class StrategyTuningWorkflowTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly StrategyTuningReportWorkflow _workflow = new(
        new StrategyTuningAnalysisService(new StrategyComparisonReportService()));

    public StrategyTuningWorkflowTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Parse_AbsentOptions_LeavesNormalWorkflowUnchanged()
    {
        Assert.True(StrategyTuningCommandLineParser.TryParse(["--run-variants"], out var input, out var output, out var error));
        Assert.Null(input);
        Assert.Null(output);
        Assert.Null(error);
    }

    [Fact]
    public void Parse_AcceptsCaseInsensitiveOptionsAndPathsWithEquals()
    {
        Assert.True(StrategyTuningCommandLineParser.TryParse(
            ["--TUNING-INPUT=/tmp/input=data.json", "--EXPORT-TUNING-JSON=/tmp/research.json"],
            out var input, out var output, out var error));
        Assert.Equal("/tmp/input=data.json", input);
        Assert.Equal("/tmp/research.json", output);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--tuning-input")]
    [InlineData("--tuning-input=")]
    [InlineData("--tuning-input= ")]
    [InlineData("--tuning-input=a|--tuning-input=b")]
    [InlineData("--export-tuning-json=a")]
    [InlineData("--tuning-input=a|--export-tuning-json")]
    [InlineData("--tuning-input=a|--export-tuning-json=")]
    [InlineData("--tuning-input=a|--export-tuning-json=x|--export-tuning-json=y")]
    [InlineData("--tuning-input=a|--run-variants")]
    [InlineData("--tuning-input=a|--run-backtest")]
    [InlineData("--tuning-input=a|--backtest-run-id=1")]
    [InlineData("--tuning-input=a|--backtest-run-id")]
    [InlineData("--tuning-input=a|--export-backtest-csv=x")]
    [InlineData("--tuning-input=a|--export-backtest-snapshots-csv=x")]
    [InlineData("--tuning-input=a|--export-comparison-csv=x")]
    public void Parse_RejectsAmbiguousOrIncompleteModes(string command)
    {
        Assert.False(StrategyTuningCommandLineParser.TryParse(command.Split('|'), out _, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task RunAsync_ReadsHistoricalMetricsAndExportsResearchEvidence_WithoutMutatingInput()
    {
        var path = await WriteRequestAsync();
        var original = await File.ReadAllTextAsync(path);
        var output = Path.Combine(_directory, "research.json");

        var report = await _workflow.RunAsync(path, output);

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(output));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("heuristic-v1", root.GetProperty("method").GetString());
        Assert.Equal("SPY", root.GetProperty("benchmarkSymbol").GetString());
        Assert.Equal("Completed", root.GetProperty("analyses")[0].GetProperty("metrics").GetProperty("status").GetString());
        Assert.Equal(2, root.GetProperty("analyses").GetArrayLength());
        Assert.True(root.GetProperty("analyses")[0].GetProperty("isPromising").GetBoolean());
        Assert.NotEmpty(root.GetProperty("analyses")[0].GetProperty("configurationJson").GetString()!);
        Assert.Equal(2, report.Analyses.Count);
    }

    [Fact]
    public async Task RunAsync_ConsoleOnly_DoesNotRequireOutputFile()
    {
        var report = await _workflow.RunAsync(await WriteRequestAsync(), null);
        Assert.Equal(2, report.Analyses.Count);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task RunAsync_RejectsOverwritingInput()
    {
        var path = await WriteRequestAsync();
        var original = await File.ReadAllTextAsync(path);
        await Assert.ThrowsAsync<ArgumentException>(() => _workflow.RunAsync(path, path));
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RunAsync_PreservesExistingExportFile()
    {
        var input = await WriteRequestAsync();
        var output = Path.Combine(_directory, "existing.json");
        await File.WriteAllTextAsync(output, "existing research");
        await Assert.ThrowsAsync<IOException>(() => _workflow.RunAsync(input, output));
        Assert.Equal("existing research", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task RunAsync_PreservesInputWhenOutputIsSymbolicLink()
    {
        var input = await WriteRequestAsync();
        var original = await File.ReadAllTextAsync(input);
        var output = Path.Combine(_directory, "alias.json");
        File.CreateSymbolicLink(output, input);
        await Assert.ThrowsAsync<IOException>(() => _workflow.RunAsync(input, output));
        Assert.Equal(original, await File.ReadAllTextAsync(input));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("{\"runs\":[{}]}")]
    public async Task RunAsync_RejectsMalformedJson_WithoutExport(string contents)
    {
        var input = Path.Combine(_directory, "input.json");
        var output = Path.Combine(_directory, "research.json");
        await File.WriteAllTextAsync(input, contents);
        await Assert.ThrowsAsync<JsonException>(() => _workflow.RunAsync(input, output));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ReadAsync_PropagatesCancellationAndMissingFileErrors()
    {
        var input = await WriteRequestAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => StrategyTuningJsonFile.ReadAsync(input, cancellation.Token));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            StrategyTuningJsonFile.ReadAsync(Path.Combine(_directory, "missing.json")));
    }

    private async Task<string> WriteRequestAsync()
    {
        var path = Path.Combine(_directory, "input.json");
        var request = new StrategyTuningRequest
        {
            BenchmarkSymbol = " spy ",
            Runs = [StrategyTuningAnalysisServiceTests.Run(1, "safe"), StrategyTuningAnalysisServiceTests.Run(2, "risky", 30m, 25m)]
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(request));
        return path;
    }
}
