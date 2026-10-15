using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IStrategyTuningAnalysisService
{
    StrategyTuningReport Analyze(StrategyTuningRequest request);
}
