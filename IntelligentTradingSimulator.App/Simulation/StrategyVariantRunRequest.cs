using System;
using System.Collections.Generic;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class StrategyVariantRunRequest
{
    public string NamePrefix { get; set; } = "Variant Backtest";

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public DateTime HistoricalStartDate { get; set; }

    public decimal InitialCapital { get; set; }

    public IReadOnlyList<string> Symbols { get; set; } = [];
}
