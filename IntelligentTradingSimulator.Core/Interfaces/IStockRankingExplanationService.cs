using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

/// <summary>
/// Produces a narrative from scoring evidence. Alternative narrative generators
/// can implement this contract without changing scoring or report consumers.
/// </summary>
public interface IStockRankingExplanationService
{
    StockRankingExplanation Explain(
        ObservationResult observation,
        int? rank = null,
        ObservationResult? previous = null);
}
