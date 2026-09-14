using System;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class SimulationRunner
{
    private readonly MarketDataWorkflow _marketDataWorkflow;
    private readonly PortfolioWorkflow _portfolioWorkflow;
    private readonly ConsoleReporter _reporter;
    private readonly StrategyConfiguration _strategyConfiguration;

    public SimulationRunner(
        MarketDataWorkflow marketDataWorkflow,
        PortfolioWorkflow portfolioWorkflow,
        ConsoleReporter reporter,
        StrategyConfiguration strategyConfiguration)
    {
        _marketDataWorkflow = marketDataWorkflow;
        _portfolioWorkflow = portfolioWorkflow;
        _reporter = reporter;
        _strategyConfiguration = strategyConfiguration;
    }

    public async Task RunAsync()
    {
        var from =
            DateTime.Today.AddDays(
                -_strategyConfiguration.LookbackDays);

        var marketData =
            await _marketDataWorkflow.CollectAndAnalyzeAsync(
                StockUniverse.Symbols,
                from);

        _reporter.PrintMarketRanking(marketData.RankedResults);
        _reporter.PrintRecommendations(marketData.Recommendations);

        var portfolioState =
            await _portfolioWorkflow.RunAsync(
                marketData.Recommendations,
                marketData.Prices);

        _reporter.PrintPortfolioPerformance(portfolioState.Performance);

        _reporter.PrintPortfolioAllocation(
            portfolioState.Portfolio,
            portfolioState.TotalValue);

        _reporter.PrintTargetAllocation(
            portfolioState.Portfolio,
            marketData.Recommendations);

        _reporter.PrintRebalanceByWeight(
            portfolioState.Portfolio,
            marketData.Recommendations);

        _reporter.PrintPositionContributions(portfolioState.Portfolio);

        _reporter.PrintPreviousRunInfo(marketData.PreviousRun);
    }
}
