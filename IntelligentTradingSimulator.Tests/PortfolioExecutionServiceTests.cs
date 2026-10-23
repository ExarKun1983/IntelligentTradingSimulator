using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioExecutionServiceTests
{
    [Fact]
    public void ExecuteRecommendations_CapsAllocation_ToConfiguredMaxPositionWeight()
    {
        var service =
            new PortfolioExecutionService(
                maxPositionWeight: 30m);

        var portfolio =
            new Portfolio
            {
                StartingCash = 10_000m,
                Cash = 10_000m
            };

        var recommendations =
            new List<StockRecommendation>
            {
                new() { Symbol = "AAPL", AllocationPercent = 90m, Score = 100m },
                new() { Symbol = "MSFT", AllocationPercent = 10m, Score = 10m }
            };

        var prices =
            new Dictionary<string, decimal>
            {
                ["AAPL"] = 100m,
                ["MSFT"] = 100m
            };

        service.ExecuteRecommendations(
            portfolio,
            recommendations,
            prices,
            new TradingEngine());

        var aapl =
            portfolio.Positions
                .Single(x => x.Symbol == "AAPL");

        Assert.Equal(2_400m, aapl.MarketValue);
        Assert.Equal(6_872.8m, portfolio.Cash);
    }

    [Fact]
    public void ExecuteRecommendations_DoesNotCapAllocation_WhenBelowMaxPositionWeight()
    {
        var service =
            new PortfolioExecutionService(
                maxPositionWeight: 50m);

        var portfolio =
            new Portfolio
            {
                StartingCash = 10_000m,
                Cash = 10_000m
            };

        var recommendations =
            new List<StockRecommendation>
            {
                new() { Symbol = "AAPL", AllocationPercent = 40m, Score = 80m },
                new() { Symbol = "MSFT", AllocationPercent = 30m, Score = 60m }
            };

        var prices =
            new Dictionary<string, decimal>
            {
                ["AAPL"] = 100m,
                ["MSFT"] = 100m
            };

        service.ExecuteRecommendations(
            portfolio,
            recommendations,
            prices,
            new TradingEngine());

        var aapl =
            portfolio.Positions
                .Single(x => x.Symbol == "AAPL");

        Assert.Equal(4_000m, aapl.MarketValue);
    }

    [Fact]
    public void ExecuteRecommendations_DoesNotExceedConfiguredOpenPositionLimit()
    {
        var service = new PortfolioExecutionService(maxPositionWeight: 100m, maxPositions: 1);
        var portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m };

        service.ExecuteRecommendations(
            portfolio,
            [
                new StockRecommendation { Symbol = "AAA", AllocationPercent = 60m, Score = 60m },
                new StockRecommendation { Symbol = "BBB", AllocationPercent = 40m, Score = 40m }
            ],
            new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 100m },
            new TradingEngine());

        Assert.Equal("AAA", Assert.Single(portfolio.Positions).Symbol);
        Assert.Single(portfolio.Trades);
    }

    [Fact]
    public void ExecuteRecommendations_ReweightsAfterSelectingInitialPositions()
    {
        var service = new PortfolioExecutionService(maxPositionWeight: 100m, maxPositions: 1);
        var portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m };

        service.ExecuteRecommendations(
            portfolio,
            [
                new StockRecommendation { Symbol = "AAA", AllocationPercent = 25m, Score = 100m },
                new StockRecommendation { Symbol = "BBB", AllocationPercent = 25m, Score = 50m },
                new StockRecommendation { Symbol = "CCC", AllocationPercent = 50m, Score = 25m }
            ],
            new Dictionary<string, decimal> { ["AAA"] = 100m, ["BBB"] = 100m, ["CCC"] = 100m },
            new TradingEngine());

        Assert.Equal("AAA", Assert.Single(portfolio.Positions).Symbol);
        Assert.Equal(8_000m, portfolio.Positions[0].MarketValue);
        Assert.Equal(2_000m, portfolio.Cash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.01)]
    public void Constructor_Throws_WhenMaxPositionWeightIsOutOfRange(decimal maxPositionWeight)
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PortfolioExecutionService(
                    maxPositionWeight));

        Assert.Equal("maxPositionWeight", exception.ParamName);
        Assert.Contains("less than or equal to 100", exception.Message);
    }
}
