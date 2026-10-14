using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Training;

public static class FutureOutcomeLabelCalculator
{
    public static IReadOnlyList<FutureOutcomeLabel> Calculate(
        FeatureSnapshot snapshot,
        IReadOnlyList<MarketCandle> prices,
        IReadOnlyList<MarketCandle> benchmarkPrices,
        DateOnly availableThrough,
        string benchmarkSymbol = "SPY",
        IReadOnlyList<int>? horizons = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkSymbol);
        var requestedHorizons = horizons ?? [5, 20, 60];
        if (requestedHorizons.Any(x => x <= 0))
            throw new ArgumentOutOfRangeException(nameof(horizons), "Trading-day horizons must be positive.");

        var stock = IndexPrices(prices, snapshot.AnalysisDate, availableThrough);
        var benchmark = IndexPrices(benchmarkPrices, snapshot.AnalysisDate, availableThrough);
        if (!stock.TryGetValue(snapshot.AnalysisDate, out var start) || start <= 0)
            return [];

        var future = stock.Where(x => x.Key > snapshot.AnalysisDate).OrderBy(x => x.Key).ToArray();
        var labels = new List<FutureOutcomeLabel>();
        foreach (var horizon in requestedHorizons.Distinct().Order())
        {
            if (future.Length < horizon)
                continue;

            var end = future[horizon - 1];
            var stockReturn = ReturnPercent(start, end.Value);
            if (stockReturn is null)
                continue;

            decimal? benchmarkReturn = null;
            if (benchmark.TryGetValue(snapshot.AnalysisDate, out var benchmarkStart) &&
                benchmark.TryGetValue(end.Key, out var benchmarkEnd))
                benchmarkReturn = ReturnPercent(benchmarkStart, benchmarkEnd);

            labels.Add(new FutureOutcomeLabel
            {
                FeatureSnapshotId = snapshot.Id,
                HorizonTradingDays = horizon,
                OutcomeDate = end.Key,
                ReturnPercent = stockReturn.Value,
                BenchmarkSymbol = benchmarkSymbol.Trim().ToUpperInvariant(),
                BenchmarkReturnPercent = benchmarkReturn,
                OutperformedBenchmark = benchmarkReturn.HasValue ? stockReturn > benchmarkReturn : null
            });
        }
        return labels;
    }

    private static Dictionary<DateOnly, decimal> IndexPrices(
        IReadOnlyList<MarketCandle> prices, DateOnly from, DateOnly to) =>
        prices.Where(x => DateOnly.FromDateTime(x.Date) >= from && DateOnly.FromDateTime(x.Date) <= to)
            .ToDictionary(x => DateOnly.FromDateTime(x.Date), x => x.Close);

    private static decimal? ReturnPercent(decimal start, decimal end)
    {
        if (start <= 0 || end <= 0)
            return null;
        try
        {
            return (end / start - 1m) * 100m;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
