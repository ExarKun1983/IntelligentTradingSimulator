using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestDayRequest
{
    public int BacktestRunId { get; set; }

    public DateTime CurrentDate { get; set; }

    public DateTime HistoricalStartDate { get; set; }

    public StrategyConfiguration StrategyConfiguration { get; set; } = new();

    public Portfolio Portfolio { get; set; } = null!;

    public IReadOnlyList<string> Symbols { get; set; } = [];
}
