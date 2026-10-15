using System;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class MarketObservationAnalyzerFactory
{
    public MarketObservationAnalyzer Create(
        StrategyConfiguration strategyConfiguration)
    {
        ArgumentNullException.ThrowIfNull(strategyConfiguration);

        return new MarketObservationAnalyzer(
            ScoringEngineFactory.Create(strategyConfiguration));
    }
}
