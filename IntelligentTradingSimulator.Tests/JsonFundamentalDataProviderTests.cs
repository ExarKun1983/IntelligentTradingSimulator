using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.MarketData;

namespace IntelligentTradingSimulator.Tests;

public sealed class JsonFundamentalDataProviderTests : IDisposable
{
    private readonly string _filePath = Path.Combine(
        Path.GetTempPath(), $"{Guid.NewGuid()}.json");

    public void Dispose() => File.Delete(_filePath);

    private async Task<IFundamentalDataProvider> CreateProviderAsync(string json)
    {
        await File.WriteAllTextAsync(_filePath, json);
        return new JsonFundamentalDataProvider(_filePath);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_MapsAllSnapshotFields()
    {
        var provider = await CreateProviderAsync("""
            [{
                "Symbol": "AAPL",
                "AsOfDate": "2025-12-31",
                "RevenueGrowthPercent": 12.5,
                "EpsGrowthPercent": -3.25,
                "Margins": 21.75,
                "Debt": 1.5
            }]
            """);

        var result = await provider.GetFundamentalDataAsync(" aapl ");

        Assert.NotNull(result);
        Assert.Equal("AAPL", result.Symbol);
        Assert.Equal(new DateOnly(2025, 12, 31), result.AsOfDate);
        Assert.Equal(12.5m, result.RevenueGrowthPercent);
        Assert.Equal(-3.25m, result.EpsGrowthPercent);
        Assert.Equal(21.75m, result.Margins);
        Assert.Equal(1.5m, result.Debt);
    }

    [Theory]
    [InlineData("""[{"Symbol":"AAPL"}]""")]
    [InlineData("""[{"Symbol":"AAPL","AsOfDate":null,"RevenueGrowthPercent":null,"EpsGrowthPercent":null,"Margins":null,"Debt":null}]""")]
    public async Task GetFundamentalDataAsync_PreservesMissingFields(string json)
    {
        var provider = await CreateProviderAsync(json);

        var result = await provider.GetFundamentalDataAsync("AAPL");

        Assert.NotNull(result);
        Assert.Null(result.AsOfDate);
        Assert.Null(result.RevenueGrowthPercent);
        Assert.Null(result.EpsGrowthPercent);
        Assert.Null(result.Margins);
        Assert.Null(result.Debt);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_RoundTripsPartialDataWithoutReplacingZero()
    {
        var snapshot = new FundamentalData
        {
            Symbol = "AAPL",
            RevenueGrowthPercent = 0m,
            Margins = -1.25m,
            Debt = 0m
        };
        var provider = await CreateProviderAsync(JsonSerializer.Serialize(new[] { snapshot }));

        var result = await provider.GetFundamentalDataAsync("AAPL");

        Assert.NotNull(result);
        Assert.Equal(0m, result.RevenueGrowthPercent);
        Assert.Null(result.EpsGrowthPercent);
        Assert.Equal(-1.25m, result.Margins);
        Assert.Equal(0m, result.Debt);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"Symbol":"MSFT"}]""")]
    public async Task GetFundamentalDataAsync_ReturnsNullForMissingSymbol(string json)
    {
        var provider = await CreateProviderAsync(json);

        Assert.Null(await provider.GetFundamentalDataAsync("AAPL"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"Symbol":" "}]""")]
    [InlineData("""[{"Symbol":"AAPL"},{"Symbol":" aapl "}]""")]
    [InlineData("""[{"Symbol":"AAPL","Debt":"unavailable"}]""")]
    public async Task GetFundamentalDataAsync_RejectsInvalidSnapshots(string json)
    {
        var provider = await CreateProviderAsync(json);

        await Assert.ThrowsAsync<JsonException>(
            () => provider.GetFundamentalDataAsync("AAPL"));
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesFileErrors()
    {
        var provider = new JsonFundamentalDataProvider(_filePath);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.GetFundamentalDataAsync("AAPL"));
    }

    [Fact]
    public async Task GetFundamentalDataAsync_ReloadsUpdatedFile()
    {
        var provider = await CreateProviderAsync("""[{"Symbol":"AAPL","Debt":1}]""");
        Assert.Equal(1m, (await provider.GetFundamentalDataAsync("AAPL"))!.Debt);

        await File.WriteAllTextAsync(_filePath, """[{"Symbol":"AAPL","Debt":2}]""");

        Assert.Equal(2m, (await provider.GetFundamentalDataAsync("AAPL"))!.Debt);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_HonorsCancellation()
    {
        var provider = await CreateProviderAsync("[]");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetFundamentalDataAsync("AAPL", cancellation.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task GetFundamentalDataAsync_RejectsBlankSymbols(string? symbol)
    {
        var provider = await CreateProviderAsync("[]");

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => provider.GetFundamentalDataAsync(symbol!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_RejectsBlankFilePaths(string? filePath)
    {
        Assert.ThrowsAny<ArgumentException>(() => new JsonFundamentalDataProvider(filePath!));
    }
}
