using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntelligentTradingSimulator.Tests;

public sealed class Pr192PriceDriftAcceptanceTests
{
    [Fact]
    public async Task UnchangedTargets_AcrossPersistedRuns_ShouldNotTradePriceDrift()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase($"pr192-drift-{Guid.NewGuid()}")
            .Options;

        // Seed an existing portfolio, not the initial-purchase workflow.
        await using (var context = new TradingSimulatorDbContext(options))
        {
            await new PortfolioRepository(context).SaveAsync(new Portfolio
            {
                Name = "PR192 price-drift acceptance",
                StartingCash = 1_000m,
                Cash = 0m,
                Positions =
                [
                    new Position
                    {
                        Symbol = "AAA",
                        Quantity = 5m,
                        AveragePrice = 100m,
                        CurrentPrice = 100m
                    },
                    new Position
                    {
                        Symbol = "BBB",
                        Quantity = 5m,
                        AveragePrice = 100m,
                        CurrentPrice = 100m
                    }
                ]
            });
        }

        async Task Run(decimal aaaPrice)
        {
            // Each run loads the persisted portfolio in a new context.
            await using var context = new TradingSimulatorDbContext(options);
            var allocationService = new PortfolioAllocationService();
            var configuration = new StrategyConfiguration
            {
                MaxPositions = 2,
                MaxPositionWeight = 100m,
                UseHoldingConditionExit = false,
                StopLossPercent = null,
                TrailingStopPercent = null
            };

            var workflow = new PortfolioWorkflow(
                new PortfolioRepository(context),
                new PortfolioSnapshotRepository(context),
                new PositionSnapshotRepository(context),
                new PortfolioExecutionService(
                    configuration.MaxPositionWeight,
                    configuration.MaxPositions),
                new TradingEngine(),
                new PortfolioAllocationEngine(
                    allocationService,
                    configuration.MaxPositionWeight),
                new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                new ConsoleReporter(
                    allocationService,
                    new PortfolioContributionService()),
                configuration);

            await workflow.RunAsync(
                [
                    new StockRecommendation
                    {
                        Symbol = "AAA",
                        Score = 10m,
                        AllocationPercent = 50m
                    },
                    new StockRecommendation
                    {
                        Symbol = "BBB",
                        Score = 10m,
                        AllocationPercent = 50m
                    }
                ],
                new Dictionary<string, decimal>
                {
                    ["AAA"] = aaaPrice,
                    ["BBB"] = 100m
                });
        }

        // Establish unchanged targets at the initial prices.
        await Run(100m);

        await using (var context = new TradingSimulatorDbContext(options))
        {
            var initial = await new PortfolioRepository(context).GetLatestAsync();
            Assert.NotNull(initial);
            Assert.Empty(initial.Trades);
            Assert.Equal(2, initial.Positions.Count);
            Assert.All(initial.Positions, position =>
                Assert.Equal(5m, position.Quantity));
            Assert.Equal(0m, initial.Cash);
        }

        // Only AAA's price changes; scores, targets and exit settings do not.
        await Run(200m);

        await using var readContext = new TradingSimulatorDbContext(options);
        var saved = await new PortfolioRepository(readContext).GetLatestAsync();
        Assert.NotNull(saved);

        var tradeDetails = string.Join("; ", saved.Trades.Select(trade =>
            $"{trade.Action} {trade.Symbol}: qty={trade.Quantity}, " +
            $"price={trade.Price}, reason={trade.ExitReason}"));

        var positionDetails = string.Join("; ", saved.Positions.Select(position =>
            $"{position.Symbol}: qty={position.Quantity}"));

        Assert.True(
            saved.Trades.Count == 0,
            $"Price-only change generated trades: {tradeDetails}. " +
            $"Positions: {positionDetails}. Cash={saved.Cash}");

        Assert.Equal(2, saved.Positions.Count);
        Assert.Equal(5m, saved.Positions.Single(x => x.Symbol == "AAA").Quantity);
        Assert.Equal(5m, saved.Positions.Single(x => x.Symbol == "BBB").Quantity);
        Assert.Equal(0m, saved.Cash);
    }
}
