using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioRecommendationServiceTests
{
    private readonly PortfolioRecommendationService _service = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Build_UsesScoreThenOrdinalSymbol_BeforeLimitingPositions(bool reverseInput)
    {
        var results = new List<ObservationResult>
        {
            new() { Symbol = "aaa", Score = 10m, MomentumPercent = 20m, IsAboveSma5 = true },
            new() { Symbol = "ZZZ", Score = 10m, MomentumPercent = 10m, IsAboveSma5 = true },
            new() { Symbol = "BBB", Score = 20m, MomentumPercent = 1m, IsAboveSma5 = true },
            new() { Symbol = "NVDA", Score = 100m, MomentumPercent = 50m, IsAboveSma5 = true },
            new() { Symbol = "INELIGIBLE", Score = 100m, MomentumPercent = -1m, IsAboveSma5 = true }
        };
        if (reverseInput)
        {
            results.Reverse();
        }

        var recommendations = new PortfolioRecommendationService(maxPositions: 2).Build(results);

        Assert.Equal(["BBB", "ZZZ"], recommendations.Select(x => x.Symbol).ToArray());
        Assert.Equal([20m, 10m], recommendations.Select(x => x.Score).ToArray());
        Assert.Equal([66.67m, 33.33m], recommendations.Select(x => x.AllocationPercent).ToArray());
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenNoResultsQualify()
    {
        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAA", Score = -5m, MomentumPercent = -1m, IsAboveSma5 = false }
        };

        var recommendations = _service.Build(results);

        Assert.Empty(recommendations);
    }

    [Fact]
    public void Build_ReturnsRecommendations_WhenResultsQualify()
    {
        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAPL", Score = 10m, MomentumPercent = 5m, IsAboveSma5 = true },
            new() { Symbol = "MSFT", Score = 8m, MomentumPercent = 3m, IsAboveSma5 = true }
        };

        var recommendations = _service.Build(results);

        Assert.Equal(2, recommendations.Count);
    }

    [Fact]
    public void Build_AllocationPercentsSum_ToOneHundred()
    {
        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAPL", Score = 10m, MomentumPercent = 5m, IsAboveSma5 = true },
            new() { Symbol = "MSFT", Score = 8m, MomentumPercent = 3m, IsAboveSma5 = true },
            new() { Symbol = "GOOG", Score = 6m, MomentumPercent = 2m, IsAboveSma5 = true }
        };

        var recommendations = _service.Build(results);

        var total = recommendations.Sum(r => r.AllocationPercent);

        Assert.Equal(100m, total, precision: 1);
    }

    [Fact]
    public void Build_ExcludesNvda()
    {
        var results = new List<ObservationResult>
        {
            new() { Symbol = "NVDA", Score = 100m, MomentumPercent = 50m, IsAboveSma5 = true },
            new() { Symbol = "AAPL", Score = 10m, MomentumPercent = 5m, IsAboveSma5 = true }
        };

        var recommendations = _service.Build(results);

        Assert.DoesNotContain(recommendations, r => r.Symbol == "NVDA");
    }

    [Fact]
    public void Build_LimitsRecommendations_ToConfiguredMaxPositions()
    {
        var service = new PortfolioRecommendationService(maxPositions: 2);

        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAPL", Score = 40m, MomentumPercent = 5m, IsAboveSma5 = true },
            new() { Symbol = "MSFT", Score = 30m, MomentumPercent = 4m, IsAboveSma5 = true },
            new() { Symbol = "GOOG", Score = 20m, MomentumPercent = 3m, IsAboveSma5 = true },
            new() { Symbol = "AMZN", Score = 10m, MomentumPercent = 2m, IsAboveSma5 = true }
        };

        var recommendations = service.Build(results);

        Assert.Equal(2, recommendations.Count);
        Assert.Equal(["AAPL", "MSFT"], recommendations.Select(x => x.Symbol).ToArray());
    }

    [Fact]
    public void BuildCandidates_ReturnsTheFullEligibleRanking_SeparatelyFromTheRecommendationLimit()
    {
        var service = new PortfolioRecommendationService(maxPositions: 2);
        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAPL", Score = 40m, MomentumPercent = 5m, IsAboveSma5 = true },
            new() { Symbol = "MSFT", Score = 30m, MomentumPercent = 4m, IsAboveSma5 = true },
            new() { Symbol = "GOOG", Score = 20m, MomentumPercent = 3m, IsAboveSma5 = true }
        };

        var candidates = service.BuildCandidates(results);

        Assert.Equal(["AAPL", "MSFT", "GOOG"], candidates.Select(x => x.Symbol).ToArray());
        Assert.Equal(100m, candidates.Sum(x => x.AllocationPercent), precision: 1);
        Assert.Equal(["AAPL", "MSFT"], service.Build(results).Select(x => x.Symbol).ToArray());
    }

    [Fact]
    public void Build_UsesAllAvailable_WhenFewerThanConfiguredMaxPositions()
    {
        var service = new PortfolioRecommendationService(maxPositions: 5);

        var results = new List<ObservationResult>
        {
            new() { Symbol = "AAPL", Score = 10m, MomentumPercent = 5m, IsAboveSma5 = true },
            new() { Symbol = "MSFT", Score = 8m, MomentumPercent = 3m, IsAboveSma5 = true }
        };

        var recommendations = service.Build(results);

        Assert.Equal(2, recommendations.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_Throws_WhenMaxPositionsIsNotPositive(int maxPositions)
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PortfolioRecommendationService(maxPositions));

        Assert.Equal("maxPositions", exception.ParamName);
        Assert.Contains("greater than zero", exception.Message);
    }
}
