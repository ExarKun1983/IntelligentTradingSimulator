using System;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantRunResult
{
    public string StrategyId { get; set; } = string.Empty;

    public DateTime HistoricalStartDate { get; set; }

    public BacktestEngineResult EngineResult { get; set; } = new();
}
