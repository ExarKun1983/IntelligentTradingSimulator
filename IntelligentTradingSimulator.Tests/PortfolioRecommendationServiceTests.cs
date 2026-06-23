using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioRecommendationServiceTests
{
    private readonly PortfolioRecommendationService _service = new();

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
}
