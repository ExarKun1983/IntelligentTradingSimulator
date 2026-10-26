using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class Pr192DeferredCandidateAcceptanceTests
{
    [Theory]
    [InlineData(true, "BBB")]
    [InlineData(false, "AAA")]
    public void DeferredPurchase_FreshObservationCompetes_MissingObservationReserves(
        bool aaaHasFreshObservation,
        string expectedSymbol)
    {
        var friday = new DateTime(2024, 1, 5);
        var monday = friday.AddDays(3);
        var portfolio = new Portfolio
        {
            StartingCash = 1_000m,
            Cash = 1_000m
        };
        var configuration = new StrategyConfiguration
        {
            MaxPositions = 1,
            MaxPositionWeight = 100m,
            UseHoldingConditionExit = false
        };
        var state = new BacktestExecutionState();
        var recommendations = new PortfolioRecommendationService(1);
        var allocationEngine =
            new PortfolioAllocationEngine(new PortfolioAllocationService());
        var tradingEngine = new TradingEngine();

        var fridayObservations = new[]
        {
            QualifyingObservation("AAA", 1m)
        };

        state.GenerateAfterClose(
            portfolio,
            friday,
            new Dictionary<string, decimal> { ["AAA"] = 100m },
            recommendations.BuildCandidates(fridayObservations),
            fridayObservations,
            configuration);

        Assert.Equal("AAA", Assert.Single(state.Pending).Key);
        Assert.Equal(friday, state.Pending["AAA"].SignalDate);

        state.ExecuteOpen(
            portfolio,
            monday,
            new Dictionary<string, decimal>(),
            allocationEngine,
            tradingEngine,
            configuration.MaxPositions);

        Assert.Empty(portfolio.Trades);
        Assert.Empty(portfolio.Positions);

        var mondayObservations = new List<ObservationResult>
        {
            QualifyingObservation("BBB", 100m)
        };
        var mondayCloses = new Dictionary<string, decimal>
        {
            ["BBB"] = 100m
        };

        if (aaaHasFreshObservation)
        {
            mondayObservations.Add(QualifyingObservation("AAA", 1m));
            mondayCloses["AAA"] = 100m;
        }

        state.GenerateAfterClose(
            portfolio,
            monday,
            mondayCloses,
            recommendations.BuildCandidates(mondayObservations),
            mondayObservations,
            configuration);

        var selected = Assert.Single(state.Pending);
        Assert.Equal(expectedSymbol, selected.Key);
        Assert.Equal(
            aaaHasFreshObservation ? monday : friday,
            selected.Value.SignalDate);
        Assert.Equal(100m, selected.Value.AllocationPercent);
        Assert.Empty(portfolio.Trades);

        if (aaaHasFreshObservation)
        {
            state.ExecuteOpen(
                portfolio,
                monday,
                new Dictionary<string, decimal> { [expectedSymbol] = 100m },
                allocationEngine,
                tradingEngine,
                configuration.MaxPositions);
            Assert.Empty(portfolio.Trades);
            Assert.Equal(expectedSymbol, Assert.Single(state.Pending).Key);
        }

        state.ExecuteOpen(
            portfolio,
            monday.AddDays(1),
            new Dictionary<string, decimal>
            {
                ["AAA"] = 100m,
                ["BBB"] = 100m
            },
            allocationEngine,
            tradingEngine,
            configuration.MaxPositions);

        var purchase = Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Buy, purchase.Action);
        Assert.Equal(expectedSymbol, purchase.Symbol);
        Assert.Equal(monday.AddDays(1), purchase.Timestamp);
        Assert.Equal(expectedSymbol, Assert.Single(portfolio.Positions).Symbol);
        Assert.Equal(10m, portfolio.Positions.Single().Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }

    [Theory]
    [InlineData("AAA", false)]
    [InlineData("AAA", true)]
    [InlineData("ZZZ", false)]
    [InlineData("ZZZ", true)]
    public void FreshDeferredPurchases_UseOrdinalTiesAndDeduplicateSymbols(
        string deferredSymbol, bool reverseInput)
    {
        var friday = new DateTime(2024, 1, 5);
        var monday = friday.AddDays(3);
        var portfolio = new Portfolio { StartingCash = 1_000m, Cash = 1_000m };
        var configuration = new StrategyConfiguration { MaxPositions = 1, MaxPositionWeight = 100m };
        var state = new BacktestExecutionState();
        var recommendationService = new PortfolioRecommendationService(1);
        var prices = new Dictionary<string, decimal> { ["AAA"] = 100m, ["ZZZ"] = 100m };
        var initial = new[] { QualifyingObservation(deferredSymbol, 1m) };
        state.GenerateAfterClose(portfolio, friday, prices,
            recommendationService.BuildCandidates(initial), initial, configuration);
        var originalIntent = state.Pending[deferredSymbol];
        var observations = new[]
        {
            QualifyingObservation("AAA", 1m),
            QualifyingObservation("ZZZ", 1m)
        };
        var candidates = recommendationService.BuildCandidates(observations).ToList();
        candidates.Add(new StockRecommendation { Symbol = "aaa", Score = 1m });
        if (reverseInput)
        {
            candidates.Reverse();
        }

        state.GenerateAfterClose(portfolio, monday, prices, candidates, observations, configuration);

        var selected = Assert.Single(state.Pending);
        Assert.Equal("AAA", selected.Key);
        Assert.Equal(100m, selected.Value.AllocationPercent);
        Assert.Equal(deferredSymbol == "AAA" ? friday : monday, selected.Value.SignalDate);
        if (deferredSymbol == "AAA")
        {
            Assert.Equal(originalIntent, selected.Value);
        }
        Assert.Empty(portfolio.Trades);
        state.ExecuteOpen(portfolio, monday.AddDays(1), prices,
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine(), 1);
        Assert.Equal("AAA", Assert.Single(portfolio.Trades).Symbol);
        Assert.Equal(10m, Assert.Single(portfolio.Positions).Quantity);
        Assert.Equal(0m, portfolio.Cash);
    }

    private static ObservationResult QualifyingObservation(
        string symbol,
        decimal score) => new()
    {
        Symbol = symbol,
        Score = score,
        MomentumPercent = 5m,
        IsAboveSma5 = true
    };
}
