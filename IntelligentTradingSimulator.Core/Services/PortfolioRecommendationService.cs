using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioRecommendationService
{
    private readonly int _maxPositions;

    public PortfolioRecommendationService(int maxPositions = 10)
    {
        _maxPositions = Math.Max(0, maxPositions);
    }

    public IReadOnlyList<StockRecommendation> Build(
        IReadOnlyList<ObservationResult> results)
    {
        if (_maxPositions == 0)
        {
            return [];
        }

        var topStocks =
            results
                .Where(x =>
                    x.Score > 0 &&
                    x.MomentumPercent > 0 &&
                    x.IsAboveSma5)
                .OrderByDescending(x => x.Score)
                .Where(x => x.Symbol != "NVDA")
                .Take(_maxPositions)
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