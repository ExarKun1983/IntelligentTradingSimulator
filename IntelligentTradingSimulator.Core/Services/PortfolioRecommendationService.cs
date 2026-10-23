using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioRecommendationService
{
    private readonly int _maxPositions;

    public PortfolioRecommendationService(
        int maxPositions = 10)
    {
        if (maxPositions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPositions),
                maxPositions,
                "Max positions must be greater than zero.");
        }

        _maxPositions = maxPositions;
    }

    public IReadOnlyList<StockRecommendation> Build(
        IReadOnlyList<ObservationResult> results)
    {
        return BuildRecommendations(BuildCandidates(results).Take(_maxPositions));
    }

    public IReadOnlyList<StockRecommendation> BuildCandidates(
        IReadOnlyList<ObservationResult> results)
    {
        return BuildRecommendations(
            results
                .Where(x =>
                    x.Score > 0 &&
                    x.MomentumPercent > 0 &&
                    x.IsAboveSma5 &&
                    !string.Equals(x.Symbol, "NVDA", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(x => x.Score)
                    .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                    .First())
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Symbol, StringComparer.Ordinal));
    }

    private static IReadOnlyList<StockRecommendation> BuildRecommendations(
        IEnumerable<ObservationResult> stocks)
    {
        return BuildRecommendations(stocks.Select(x => (x.Symbol, x.Score)));
    }

    private static IReadOnlyList<StockRecommendation> BuildRecommendations(
        IEnumerable<StockRecommendation> stocks)
    {
        return BuildRecommendations(stocks.Select(x => (x.Symbol, x.Score)));
    }

    private static IReadOnlyList<StockRecommendation> BuildRecommendations(
        IEnumerable<(string Symbol, decimal Score)> stocks)
    {
        var selectedStocks = stocks.ToList();
        if (selectedStocks.Count == 0)
        {
            return [];
        }

        var totalScore = selectedStocks.Sum(x => x.Score);

        return selectedStocks
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