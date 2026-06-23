using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioRecommendationService
{
    public IReadOnlyList<StockRecommendation> Build(
        IReadOnlyList<ObservationResult> results)
    {
        var topStocks =
            results
                .Where(x =>
                    x.Score > 0 &&
                    x.MomentumPercent > 0 &&
                    x.IsAboveSma5)
                .OrderByDescending(x => x.Score)
                .Where(x => x.Symbol != "NVDA")
                .Take(10)
                .ToList();

        if (!topStocks.Any())
        {
            return [];
        }

        var totalScore =
            topStocks.Sum(x => x.Score);

        return topStocks
            .Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,

                AllocationPercent =
                    Math.Round(
                        x.Score / totalScore * 100m,
                        2),

                Score = x.Score
            })
            .ToList();
    }
}