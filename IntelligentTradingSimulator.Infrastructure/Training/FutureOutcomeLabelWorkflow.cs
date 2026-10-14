using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Training;

namespace IntelligentTradingSimulator.Infrastructure.Training;

public sealed class FutureOutcomeLabelWorkflow(
    IFeatureSnapshotRepository snapshots,
    IFutureOutcomeLabelRepository labels,
    IMarketDataProvider marketData)
{
    public async Task GenerateAsync(
        DateOnly from,
        DateOnly to,
        DateOnly availableThrough,
        string benchmarkSymbol = "SPY",
        IReadOnlyList<int>? horizons = null,
        string? symbol = null,
        string? strategyId = null,
        int? backtestRunId = null)
    {
        if (from > to || to > availableThrough)
            throw new ArgumentException("Snapshot dates must be ordered and not after the historical data cutoff.");
        ArgumentException.ThrowIfNullOrWhiteSpace(benchmarkSymbol);
        if (horizons?.Any(x => x <= 0) == true)
            throw new ArgumentOutOfRangeException(nameof(horizons));
        benchmarkSymbol = benchmarkSymbol.Trim().ToUpperInvariant();

        var stored = await snapshots.QueryAsync(from, to, symbol, strategyId, backtestRunId);
        if (stored.Count == 0)
            return;
        var benchmark = await marketData.GetHistoricalDailyAsync(
            benchmarkSymbol, from.ToDateTime(TimeOnly.MinValue), availableThrough.ToDateTime(TimeOnly.MinValue));

        foreach (var group in stored.GroupBy(x => x.Symbol))
        {
            var prices = await marketData.GetHistoricalDailyAsync(
                group.Key, from.ToDateTime(TimeOnly.MinValue), availableThrough.ToDateTime(TimeOnly.MinValue));
            foreach (var snapshot in group)
            {
                var generated = FutureOutcomeLabelCalculator.Calculate(
                    snapshot, prices, benchmark, availableThrough, benchmarkSymbol, horizons);
                await labels.SaveAsync(generated);
            }
        }
    }
}
