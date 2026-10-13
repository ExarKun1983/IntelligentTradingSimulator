using IntelligentTradingSimulator.App.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSummaryCsvCommandLineParserTests
{
    [Theory]
    [InlineData("--run-backtest")]
    [InlineData("--backtest-run-id=42")]
    [InlineData("--RUN-BACKTEST")]
    public void TryParse_AcceptsSingleRunModesAndPreservesPath(string mode)
    {
        Assert.True(BacktestSummaryCsvCommandLineParser.TryParse(
            [mode, "--EXPORT-BACKTEST-CSV=results with spaces.csv"], out var path, out var error));
        Assert.Equal("results with spaces.csv", path);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_WithoutExportPreservesExistingCommands()
    {
        Assert.True(BacktestSummaryCsvCommandLineParser.TryParse(
            ["--run-variants"], out var path, out var error));
        Assert.Null(path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--export-backtest-csv")]
    [InlineData("--export-backtest-csv=")]
    [InlineData("--export-backtest-csv=   ")]
    public void TryParse_RejectsMissingOrBlankPath(string argument)
    {
        Assert.False(BacktestSummaryCsvCommandLineParser.TryParse(
            ["--run-backtest", argument], out var path, out var error));
        Assert.Null(path);
        Assert.Contains("non-empty path", error);
    }

    [Theory]
    [InlineData("--run-variants")]
    [InlineData("--backtest-run-id=bad")]
    [InlineData("--backtest-run-id=0")]
    [InlineData("--backtest-run-id=-1")]
    public void TryParse_RejectsUnsupportedOrInvalidRunMode(string mode)
    {
        Assert.False(BacktestSummaryCsvCommandLineParser.TryParse(
            [mode, "--export-backtest-csv=results.csv"], out var path, out var error));
        Assert.Null(path);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryParse_RejectsExportWithoutBacktestCommand()
    {
        Assert.False(BacktestSummaryCsvCommandLineParser.TryParse(
            ["--export-backtest-csv=results.csv"], out _, out var error));
        Assert.Contains("requires --run-backtest", error);
    }

    [Fact]
    public void TryParse_RejectsDuplicateOptions()
    {
        Assert.False(BacktestSummaryCsvCommandLineParser.TryParse(
            ["--run-backtest", "--export-backtest-csv=one.csv", "--export-backtest-csv=two.csv"],
            out _, out var error));
        Assert.Contains("exactly once", error);
    }
}
