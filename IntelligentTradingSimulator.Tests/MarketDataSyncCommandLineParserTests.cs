using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class MarketDataSyncCommandLineParserTests
{
    [Fact]
    public void TryParse_NotRequested_PreservesExistingCommands()
    {
        Assert.True(MarketDataSyncCommandLineParser.TryParse(["--run-backtest"],
            out var synchronize, out _, out _, out _, out var error));
        Assert.False(synchronize);
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_DefaultUniverseIncludesBacktestStocksAndBenchmarks()
    {
        Assert.True(MarketDataSyncCommandLineParser.TryParse(
            ["--sync-market-data", "--from=2019-01-01", "--to=2024-12-31"],
            out var synchronize, out var from, out var to, out var symbols, out var error));
        Assert.True(synchronize);
        Assert.Equal(new DateTime(2019, 1, 1), from);
        Assert.Equal(new DateTime(2024, 12, 31), to);
        Assert.All(StockUniverse.Symbols.Concat(BenchmarkUniverse.DefaultSymbols), x => Assert.Contains(x, symbols));
        Assert.Null(error);
    }

    [Fact]
    public void TryParse_NormalizesAndDeduplicatesCustomSymbols()
    {
        Assert.True(MarketDataSyncCommandLineParser.TryParse(
            ["--SYNC-MARKET-DATA", "--from=2024-01-01", "--to=2024-01-01", "--symbols= aapl,MSFT,AAPL"],
            out _, out _, out _, out var symbols, out _));
        Assert.Equal(new[] { "AAPL", "MSFT" }, symbols);
    }

    [Theory]
    [InlineData("--from=bad", "--to=2024-01-01")]
    [InlineData("--from=2024-01-02", "--to=2024-01-01")]
    [InlineData("--from=2024-01-01", "--to=9999-12-31")]
    [InlineData("--from=2024-01-01", "--to=")]
    [InlineData("--from=2024-01-01", "--symbols=")]
    [InlineData("--from=2024-01-01", "--to=2024-01-01", "--symbols=,AAPL")]
    public void TryParse_InvalidInputReturnsError(params string[] options)
    {
        Assert.False(MarketDataSyncCommandLineParser.TryParse(["--sync-market-data", .. options],
            out _, out _, out _, out _, out var error));
        Assert.NotNull(error);
    }
}
