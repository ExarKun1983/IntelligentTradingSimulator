using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantRunner : IStrategyVariantRunner
{
    private readonly IBacktestEngine _engine;
    private readonly IWorkflowProgress _progress;

    public StrategyVariantRunner(IBacktestEngine engine, IWorkflowProgress? progress = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _progress = progress ?? NullWorkflowProgress.Instance;
    }

    public async Task<StrategyVariantBatchResult> RunAsync(
        IReadOnlyList<StrategyConfiguration> variants,
        StrategyVariantRunRequest request)
    {
        if (variants is null)
            throw new ArgumentNullException(nameof(variants));

        if (request is null)
            throw new ArgumentNullException(nameof(request));

        var results = new List<StrategyVariantRunResult>(variants.Count);

        for (var variantIndex = 0; variantIndex < variants.Count; variantIndex++)
        {
            var variant = variants[variantIndex];
            _progress.Context = _progress.Context with
            {
                Workflow = "backtest",
                StrategyId = variant.Id,
                VariantIndex = variantIndex + 1,
                TotalVariants = variants.Count,
                RunId = null,
                Date = null,
                CompletedDates = null,
                TotalDates = null,
                CompletedSymbols = null,
                TotalSymbols = null
            };
            _progress.Report("Starting strategy variant", force: true);
            var factory = new BacktestEngineRequestFactory(variant);

            var engineRequest = factory.Create(
                name: $"{request.NamePrefix} [{variant.Id}]",
                startDate: request.StartDate,
                endDate: request.EndDate,
                historicalStartDate: request.HistoricalStartDate,
                initialCapital: request.InitialCapital,
                symbols: request.Symbols);

            BacktestEngineResult engineResult;
            try
            {
                engineResult = await _engine.RunAsync(engineRequest);
            }
            catch (Exception ex)
            {
                engineResult = new BacktestEngineResult
                {
                    Status = BacktestRunStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }

            _progress.Context = _progress.Context with
            {
                RunId = engineResult.RunId > 0 ? engineResult.RunId : _progress.Context.RunId
            };
            _progress.Report(
                engineResult.Status == BacktestRunStatus.Completed
                    ? "Strategy variant complete"
                    : "Strategy variant failed",
                force: true);

            results.Add(new StrategyVariantRunResult
            {
                StrategyId = variant.Id,
                EngineResult = engineResult
            });
        }

        return new StrategyVariantBatchResult { Results = results };
    }
}
