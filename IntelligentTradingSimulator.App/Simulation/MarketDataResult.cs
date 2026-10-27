using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record MarketDataResult(
    IReadOnlyList<ObservationResult> RankedResults,
    IReadOnlyList<StockRecommendation> Recommendations,
    IReadOnlyDictionary<string, decimal> Prices,
    ObservationRun? PreviousRun)
{
    public CompletedMarketSession? ExecutionSession { get; init; }
    public string? ExecutionRejection { get; init; }
}
