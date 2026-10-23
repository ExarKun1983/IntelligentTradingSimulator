using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestDayResult
{
    public DateTime CurrentDate { get; set; }

    public Portfolio Portfolio { get; set; } = null!;

    public BacktestSnapshot Snapshot { get; set; } = null!;

    public IReadOnlyList<StockRecommendation> Recommendations { get; set; } = [];

    public IReadOnlyList<ObservationResult> Observations { get; set; } = [];

    public IReadOnlyList<string> Diagnostics { get; set; } = [];
}
