using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record MarketDataResult(
    IReadOnlyList<ObservationResult> RankedResults,
    IReadOnlyList<StockRecommendation> Recommendations,
    IReadOnlyDictionary<string, decimal> Prices,
    ObservationRun? PreviousRun);
