using System;
using System.Threading.Tasks;
using System.Threading;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class SimulationRunner
{
    private readonly MarketDataWorkflow _marketDataWorkflow;
    private readonly PortfolioWorkflow _portfolioWorkflow;
    private readonly ConsoleReporter _reporter;
    private readonly StrategyConfiguration _strategyConfiguration;
    private readonly IWorkflowProgress _progress;
    private readonly TimeProvider _timeProvider;

    public SimulationRunner(
        MarketDataWorkflow marketDataWorkflow,
        PortfolioWorkflow portfolioWorkflow,
        ConsoleReporter reporter,
        StrategyConfiguration strategyConfiguration,
        IWorkflowProgress? progress = null,
        TimeProvider? timeProvider = null)
    {
        _marketDataWorkflow = marketDataWorkflow;
        _portfolioWorkflow = portfolioWorkflow;
        _reporter = reporter;
        _strategyConfiguration = strategyConfiguration;
        _progress = progress ?? NullWorkflowProgress.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _progress.Context = _progress.Context with { StrategyId = _strategyConfiguration.Id };
        _progress.Report("Starting simulation.", force: true);
        var from =
            _timeProvider.GetLocalNow().Date.AddDays(
                -_strategyConfiguration.LookbackDays);

        MarketDataResult marketData;
        using (_progress.BeginOperation("Collecting and analyzing simulation market data"))
            marketData = await _marketDataWorkflow.CollectAndAnalyzeCompletedSessionAsync(
                StockUniverse.Symbols,
                from, cancellationToken);

        _reporter.PrintMarketRanking(marketData.RankedResults);
        _reporter.PrintRecommendations(marketData.Recommendations);
        if (marketData.ExecutionSession is null)
        {
            Console.WriteLine($"Paper execution refused: {marketData.ExecutionRejection}");
            return;
        }

        PortfolioState portfolioState;
        using (_progress.BeginOperation("Processing simulation portfolio"))
            portfolioState = await _portfolioWorkflow.RunAsync(
                marketData.Recommendations,
                marketData.ExecutionSession, cancellationToken);
        Console.WriteLine($"Paper execution: {portfolioState.Outcome}; session {marketData.ExecutionSession.SessionDate:yyyy-MM-dd}.");
        if (portfolioState.Outcome != PaperExecutionOutcome.Executed)
            return;

        _reporter.PrintPortfolioPerformance(portfolioState.Performance);

        _reporter.PrintPortfolioAllocation(
            portfolioState.Portfolio,
            portfolioState.TotalValue);

        _reporter.PrintTargetAllocation(
            portfolioState.Portfolio,
            portfolioState.ExecutionTargets);

        _reporter.PrintRebalanceByWeight(
            portfolioState.Portfolio,
            portfolioState.ExecutionTargets);

        _reporter.PrintPositionContributions(portfolioState.Portfolio);

        _reporter.PrintPreviousRunInfo(marketData.PreviousRun);
    }
}
