using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public interface IStrategyVariantRunner
{
    Task<StrategyVariantBatchResult> RunAsync(
        IReadOnlyList<StrategyConfiguration> variants,
        StrategyVariantRunRequest request);
}
