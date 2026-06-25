using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioAllocationEngineTests
{
    private static PortfolioAllocationEngine CreateEngine(decimal maxPositionWeight = 100m)
    {
        return new PortfolioAllocationEngine(
            new PortfolioAllocationService(),
            maxPositionWeight);
    }

    private static Portfolio CreatePortfolio(decimal cash = 10_000m)
    {
        return new Portfolio
        {
            Cash = cash,
            Positions = []
        };
    }

    [Fact]
    public void Calculate_CapsAllocation_ToConfiguredMaxPositionWeight()
    {
        var engine = CreateEngine(maxPositionWeight: 30m);
        var portfolio = CreatePortfolio(cash: 10_000m);

        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "AAPL", AllocationPercent = 90.91m, Score = 100m },
            new() { Symbol = "MSFT", AllocationPercent = 9.09m,  Score = 10m  }
        };

        var instructions = engine.Calculate(portfolio, recommendations);

        var aaplInstruction =
            instructions.FirstOrDefault(x => x.Symbol == "AAPL");

        Assert.NotNull(aaplInstruction);

        // With a 10_000 portfolio and 30% cap, target = 3_000
        Assert.Equal(3_000m, aaplInstruction.Difference);
    }

    [Fact]
    public void Calculate_DoesNotCapAllocation_WhenBelowMaxPositionWeight()
    {
        var engine = CreateEngine(maxPositionWeight: 50m);
        var portfolio = CreatePortfolio(cash: 10_000m);

        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "AAPL", AllocationPercent = 40m, Score = 80m },
            new() { Symbol = "MSFT", AllocationPercent = 30m, Score = 60m }
        };

        var instructions = engine.Calculate(portfolio, recommendations);

        var aaplInstruction =
            instructions.FirstOrDefault(x => x.Symbol == "AAPL");

        Assert.NotNull(aaplInstruction);

        // AllocationPercent 40m < cap 50m: target = 4_000
        Assert.Equal(4_000m, aaplInstruction.Difference);
    }

    [Fact]
    public void Calculate_ExcessRemainsAsCash_WhenCapApplied()
    {
        // With a 40% cap on a 10_000 portfolio:
        // AAPL capped at 40% → target 4_000
        // MSFT at 9.09% → target  909
        // Total allocated ≈ 4_909, meaning ~5_091 stays as cash.
        var engine = CreateEngine(maxPositionWeight: 40m);
        var portfolio = CreatePortfolio(cash: 10_000m);

        var recommendations = new List<StockRecommendation>
        {
            new() { Symbol = "AAPL", AllocationPercent = 90.91m, Score = 100m },
            new() { Symbol = "MSFT", AllocationPercent = 9.09m,  Score = 10m  }
        };

        var instructions = engine.Calculate(portfolio, recommendations);

        var totalAllocated =
            instructions
                .Where(x => x.IsBuy)
                .Sum(x => x.Difference);

        Assert.True(
            totalAllocated < portfolio.Cash,
            "Total allocated should be less than available cash when cap leaves excess.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100.01)]
    public void Constructor_Throws_WhenMaxPositionWeightIsOutOfRange(decimal maxPositionWeight)
    {
        var exception =
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PortfolioAllocationEngine(
                    new PortfolioAllocationService(),
                    maxPositionWeight));

        Assert.Equal("maxPositionWeight", exception.ParamName);
        Assert.Contains("less than or equal to 100", exception.Message);
    }
}
