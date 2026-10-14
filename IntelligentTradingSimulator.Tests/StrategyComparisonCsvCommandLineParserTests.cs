using IntelligentTradingSimulator.App.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyComparisonCsvCommandLineParserTests
{
    [Fact]
    public void TryParse_NoExportOptionPreservesExistingBehavior()
    {
        Assert.True(StrategyComparisonCsvCommandLineParser.TryParse(
            ["--run-backtest"], out var path, out var error));
        Assert.Null(path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--export-comparison-csv=reports/comparison.csv", "--run-variants")]
    [InlineData("--EXPORT-COMPARISON-CSV=reports/comparison.csv", "--RUN-VARIANTS")]
    public void TryParse_AcceptsVariantsExport(string option, string mode)
    {
        Assert.True(StrategyComparisonCsvCommandLineParser.TryParse(
            [mode, option], out var path, out var error));
        Assert.Equal("reports/comparison.csv", path);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("--export-comparison-csv")]
    [InlineData("--export-comparison-csv=")]
    [InlineData("--export-comparison-csv=   ")]
    public void TryParse_RejectsMissingPath(string option)
    {
        Assert.False(StrategyComparisonCsvCommandLineParser.TryParse(
            ["--run-variants", option], out var path, out var error));
        Assert.Null(path);
        Assert.Contains("non-empty path", error);
    }

    [Fact]
    public void TryParse_RejectsDuplicateExportOptions()
    {
        Assert.False(StrategyComparisonCsvCommandLineParser.TryParse(
            ["--run-variants", "--export-comparison-csv=a.csv", "--EXPORT-COMPARISON-CSV=b.csv"],
            out var path, out var error));
        Assert.Null(path);
        Assert.Contains("exactly once", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--run-backtest")]
    [InlineData("--backtest-run-id=42")]
    [InlineData("--backtest-run-id=invalid")]
    [InlineData("--backtest-run-id")]
    public void TryParse_RejectsMissingOrConflictingMode(string mode)
    {
        Assert.False(StrategyComparisonCsvCommandLineParser.TryParse(
            [mode, "--export-comparison-csv=report.csv"], out var path, out var error));
        Assert.Null(path);
        Assert.Contains("requires --run-variants", error);
    }

    [Theory]
    [InlineData("--run-backtest")]
    [InlineData("--backtest-run-id=42")]
    [InlineData("--BACKTEST-RUN-ID=invalid")]
    [InlineData("--backtest-run-id")]
    public void TryParse_RejectsModesThatTakePrecedenceOverVariants(string mode)
    {
        Assert.False(StrategyComparisonCsvCommandLineParser.TryParse(
            ["--run-variants", mode, "--export-comparison-csv=report.csv"], out var path, out var error));
        Assert.Null(path);
        Assert.NotNull(error);
    }
}
