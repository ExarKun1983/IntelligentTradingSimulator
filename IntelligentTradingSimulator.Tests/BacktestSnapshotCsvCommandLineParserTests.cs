using IntelligentTradingSimulator.App.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSnapshotCsvCommandLineParserTests
{
    [Theory]
    [InlineData("--run-backtest")]
    [InlineData("--backtest-run-id=42")]
    [InlineData("--BACKTEST-RUN-ID=42")]
    public void TryParse_AcceptsSingleRunModesAndPreservesPath(string mode)
    {
        Assert.True(BacktestSnapshotCsvCommandLineParser.TryParse(
            [mode, "--EXPORT-BACKTEST-SNAPSHOTS-CSV=results with spaces.csv"], out var path, out var error));
        Assert.Equal("results with spaces.csv", path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--run-variants")]
    [InlineData("--backtest-run-id=bad")]
    [InlineData("--export-backtest-csv=summary.csv")]
    public void TryParse_WithoutSnapshotExportPreservesExistingCommands(string argument)
    {
        Assert.True(BacktestSnapshotCsvCommandLineParser.TryParse(
            [argument], out var path, out var error));
        Assert.Null(path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--export-backtest-snapshots-csv")]
    [InlineData("--export-backtest-snapshots-csv=")]
    [InlineData("--export-backtest-snapshots-csv=   ")]
    public void TryParse_RejectsMissingOrBlankPath(string argument)
    {
        Assert.False(BacktestSnapshotCsvCommandLineParser.TryParse(
            ["--run-backtest", argument], out var path, out var error));
        Assert.Null(path);
        Assert.Contains("non-empty path", error);
    }

    [Theory]
    [InlineData("--run-variants")]
    [InlineData("--backtest-run-id")]
    [InlineData("--backtest-run-id=")]
    [InlineData("--backtest-run-id=bad")]
    [InlineData("--backtest-run-id=0")]
    [InlineData("--backtest-run-id=-1")]
    [InlineData("--backtest-run-id=2147483648")]
    public void TryParse_RejectsUnsupportedOrInvalidRunMode(string mode)
    {
        Assert.False(BacktestSnapshotCsvCommandLineParser.TryParse(
            [mode, "--export-backtest-snapshots-csv=results.csv"], out var path, out var error));
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsExportWithoutBacktestCommand()
    {
        Assert.False(BacktestSnapshotCsvCommandLineParser.TryParse(
            ["--export-backtest-snapshots-csv=results.csv"], out _, out var error));
        Assert.Contains("requires --run-backtest", error);
    }

    [Fact]
    public void TryParse_RejectsDuplicateExportOptions()
    {
        Assert.False(BacktestSnapshotCsvCommandLineParser.TryParse(
            ["--run-backtest", "--export-backtest-snapshots-csv=one.csv", "--export-backtest-snapshots-csv=two.csv"],
            out _, out var error));
        Assert.Contains("exactly once", error);
    }

    [Fact]
    public void TryParse_RejectsAmbiguousRunSelection()
    {
        Assert.False(BacktestSnapshotCsvCommandLineParser.TryParse(
            ["--backtest-run-id=42", "--backtest-run-id=43", "--export-backtest-snapshots-csv=results.csv"],
            out _, out var error));
        Assert.Contains("single positive", error);
    }

    [Fact]
    public void TryParse_AcceptsSnapshotAndSummaryExportsTogether()
    {
        string[] args = ["--backtest-run-id=42", "--export-backtest-csv=summary.csv",
            "--export-backtest-snapshots-csv=snapshots.csv"];

        Assert.True(BacktestSnapshotCsvCommandLineParser.TryParse(args, out var path, out _));
        Assert.True(BacktestSummaryCsvCommandLineParser.TryParse(args, out var summaryPath, out _));
        Assert.Equal("snapshots.csv", path);
        Assert.Equal("summary.csv", summaryPath);
    }
}
