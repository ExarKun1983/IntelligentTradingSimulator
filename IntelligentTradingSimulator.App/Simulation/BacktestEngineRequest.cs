using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngineRequest
{
    public string Name { get; set; } = string.Empty;

    public string StrategyId { get; set; } = StrategyConfiguration.DefaultId;

    public StrategyConfiguration StrategyConfiguration { get; set; } = new();

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public DateTime HistoricalStartDate { get; set; }

    public decimal InitialCapital { get; set; }

    public IReadOnlyList<string> Symbols { get; set; } = [];
}
