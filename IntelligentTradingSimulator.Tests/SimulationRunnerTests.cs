using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class SimulationRunnerTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;

    public SimulationRunnerTests()
    {
        var options =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        _dbContext = new TradingSimulatorDbContext(options);
    }

    public void Dispose() => _dbContext.Dispose();

    [Theory]
    [InlineData(14)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(180)]
    public async Task RunAsync_RequestsHistoryUsingActiveStrategyLookbackDays(
        int lookbackDays)
    {
        var strategy = new StrategyConfiguration
        {
            LookbackDays = lookbackDays
        };
        var provider = new RecordingMarketDataProvider();
        var runner = BuildRunner(strategy, provider);

        await runner.RunAsync();

        Assert.NotNull(provider.LastFrom);

        var expectedFrom =
            DateTime.Today.AddDays(-lookbackDays).Date;

        // The workflow may widen the requested start date to satisfy the
        // 52-week-high history requirement, but it must never narrow it -
        // the strategy's configured LookbackDays must always be honored.
        Assert.True(
            provider.LastFrom!.Value <= expectedFrom,
            $"Expected provider to be queried from {expectedFrom:yyyy-MM-dd} " +
            $"or earlier (to cover LookbackDays={lookbackDays}), " +
            $"but was queried from {provider.LastFrom.Value:yyyy-MM-dd}.");
    }

    [Fact]
    public async Task RunAsync_DoesNotUseHardcodedFourteenDayLookback_WhenStrategyConfiguresMore()
    {
        var strategy = new StrategyConfiguration
        {
            LookbackDays = 90
        };
        var provider = new RecordingMarketDataProvider();
        var runner = BuildRunner(strategy, provider);

        await runner.RunAsync();

        var hardcodedFourteenDayFrom =
            DateTime.Today.AddDays(-14).Date;

        Assert.NotNull(provider.LastFrom);
        Assert.True(
            provider.LastFrom!.Value < hardcodedFourteenDayFrom,
            "SimulationRunner must derive its lookback from the active " +
            "StrategyConfiguration.LookbackDays, not a hardcoded 14-day window.");
    }

    private SimulationRunner BuildRunner(
        StrategyConfiguration strategy,
        IMarketDataProvider provider)
    {
        var marketDataWorkflow =
            new MarketDataWorkflow(
                provider,
                new MarketObservationAnalyzer(strategy),
                new PortfolioRecommendationService(),
                new ObservationResultRepository(_dbContext));

        var portfolioWorkflow =
            new PortfolioWorkflow(
                new PortfolioRepository(_dbContext),
                new PortfolioSnapshotRepository(_dbContext),
                new PositionSnapshotRepository(_dbContext),
                new PortfolioExecutionService(),
                new TradingEngine(),
                new PortfolioAllocationEngine(new PortfolioAllocationService()),
                new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                new ConsoleReporter(
                    new PortfolioAllocationService(),
                    new PortfolioContributionService()));

        var reporter =
            new ConsoleReporter(
                new PortfolioAllocationService(),
                new PortfolioContributionService());

        return new SimulationRunner(
            marketDataWorkflow,
            portfolioWorkflow,
            reporter,
            strategy);
    }

    private sealed class RecordingMarketDataProvider : IMarketDataProvider
    {
        public DateTime? LastFrom { get; private set; }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            LastFrom = from.Date;

            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                Array.Empty<MarketCandle>());
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
        {
            LastFrom = from.Date;

            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                Array.Empty<MarketCandle>());
        }
    }
}
