using System.Collections.Generic;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantBatchResult
{
    public IReadOnlyList<StrategyVariantRunResult> Results { get; set; } = [];
}
