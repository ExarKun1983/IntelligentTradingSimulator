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
        Assert.Equal(6_800m, portfolio.Cash);
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

        Assert.Equal(3_200m, aapl.MarketValue);
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
