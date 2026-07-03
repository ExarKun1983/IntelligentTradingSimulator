using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantRunner : IStrategyVariantRunner
{
    private readonly IBacktestEngine _engine;

    public StrategyVariantRunner(IBacktestEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
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

        foreach (var variant in variants)
        {
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

            results.Add(new StrategyVariantRunResult
            {
                StrategyId = variant.Id,
                EngineResult = engineResult
            });
        }

        return new StrategyVariantBatchResult { Results = results };
    }
}
