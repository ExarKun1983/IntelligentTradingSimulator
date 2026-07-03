namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantRunResult
{
    public string StrategyId { get; set; } = string.Empty;

    public BacktestEngineResult EngineResult { get; set; } = new();
}
