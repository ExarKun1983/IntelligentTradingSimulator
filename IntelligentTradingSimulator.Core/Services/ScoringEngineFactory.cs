using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public static class ScoringEngineFactory
{
    public static IScoringEngine Create(StrategyConfiguration strategyConfiguration)
    {
        ArgumentNullException.ThrowIfNull(strategyConfiguration);

        return strategyConfiguration.ScoringEngine switch
        {
            ScoringEngineType.Composite => new CompositeScoringEngine(strategyConfiguration),
            ScoringEngineType.RuleBased => new RuleBasedScoringEngine(strategyConfiguration),
            ScoringEngineType.AI => new AiScoringEngine(strategyConfiguration),
            _ => throw new ArgumentOutOfRangeException(
                nameof(strategyConfiguration), "Unsupported scoring engine type.")
        };
    }
}
