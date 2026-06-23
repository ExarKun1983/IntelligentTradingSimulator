using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class RebalancingService
{
    public IReadOnlyList<string> GetSymbolsToSell(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations)
    {
        var recommendedSymbols =
            recommendations
                .Select(x => x.Symbol)
                .ToHashSet();

        return portfolio.Positions
            .Where(x => !recommendedSymbols.Contains(x.Symbol))
            .Select(x => x.Symbol)
            .ToList();
    }

    public IReadOnlyList<string> GetSymbolsToBuy(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations)
    {
        var currentSymbols =
            portfolio.Positions
                .Select(x => x.Symbol)
                .ToHashSet();

        return recommendations
            .Where(x => !currentSymbols.Contains(x.Symbol))
            .Select(x => x.Symbol)
            .ToList();
    }
}