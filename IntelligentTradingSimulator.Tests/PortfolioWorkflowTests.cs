using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class PortfolioWorkflowTests
{
    [Theory]
    [InlineData(true, true, 90, true)]
    [InlineData(true, true, 91, false)]
    [InlineData(false, true, 80, false)]
    [InlineData(true, false, 80, false)]
    public async Task RunAsync_StopLossPersistsAutomaticExit_WhenEnabledAndPriceBreachesThreshold(
        bool enabled, bool hasPrice, int price, bool shouldSell)
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var context = new TradingSimulatorDbContext(options))
        {
            var repository = new PortfolioRepository(context);
            await repository.SaveAsync(new Portfolio
            {
                Name = "Current portfolio",
                StartingCash = 1000m,
                Positions = [new Position
                {
                    Symbol = "AAPL", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 80m
                }]
            });
            var workflow = new PortfolioWorkflow(
                repository,
                new PortfolioSnapshotRepository(context),
                new PositionSnapshotRepository(context),
                new PortfolioExecutionService(),
                new TradingEngine(),
                new PortfolioAllocationEngine(new PortfolioAllocationService()),
                new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
                new StrategyConfiguration { StopLossPercent = enabled ? 10m : null });
            var prices = new Dictionary<string, decimal>();
            if (hasPrice)
            {
                prices["AAPL"] = price;
            }

            await workflow.RunAsync([], prices);
        }

        await using var readContext = new TradingSimulatorDbContext(options);
        var saved = await new PortfolioRepository(readContext).GetLatestAsync();
        Assert.NotNull(saved);
        if (shouldSell)
        {
            Assert.Empty(saved.Positions);
            var sell = Assert.Single(saved.Trades);
            Assert.Equal(TradeActionType.Sell, sell.Action);
            Assert.Equal(ExitReason.StopLoss, sell.ExitReason);
            Assert.Equal(10m, sell.Quantity);
            Assert.Equal(price, sell.Price);
            Assert.Equal(price * 10m, saved.Cash);
        }
        else
        {
            Assert.Single(saved.Positions);
            Assert.Empty(saved.Trades);
            Assert.Equal(0m, saved.Cash);
        }
    }
}
