using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class FutureOutcomeLabelRepository(
    TradingSimulatorDbContext dbContext) : IFutureOutcomeLabelRepository
{
    private const int BatchSize = 500;

    public async Task SaveAsync(IReadOnlyList<FutureOutcomeLabel> labels)
    {
        foreach (var batch in labels
                     .DistinctBy(x => (x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol.ToUpperInvariant()))
                     .Chunk(BatchSize))
        {
            await SaveBatchAsync(batch);
        }
    }

    private async Task SaveBatchAsync(FutureOutcomeLabel[] batch)
    {
        if (batch.Length == 0)
            return;

        var snapshotIds = batch.Select(x => x.FeatureSnapshotId).Distinct().ToArray();
        var horizons = batch.Select(x => x.HorizonTradingDays).Distinct().ToArray();
        var benchmarkSymbols = batch.Select(x => x.BenchmarkSymbol).Distinct().ToArray();
        var existingKeys = await dbContext.FutureOutcomeLabels.AsNoTracking()
            .Where(x => snapshotIds.Contains(x.FeatureSnapshotId) &&
                        horizons.Contains(x.HorizonTradingDays) &&
                        benchmarkSymbols.Contains(x.BenchmarkSymbol))
            .Select(x => new { x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol })
            .ToListAsync();
        var knownKeys = existingKeys
            .Select(x => (x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol.ToUpperInvariant()))
            .ToHashSet();
        var newLabels = batch.Where(x => knownKeys.Add(
            (x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol.ToUpperInvariant()))).ToArray();
        if (newLabels.Length == 0)
            return;

        dbContext.FutureOutcomeLabels.AddRange(newLabels);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        finally
        {
            foreach (var label in newLabels)
                dbContext.Entry(label).State = EntityState.Detached;
        }
    }

    public async Task<List<FutureOutcomeLabel>> QueryAsync(
        int featureSnapshotId, int? horizonTradingDays = null, string? benchmarkSymbol = null)
    {
        var query = dbContext.FutureOutcomeLabels.AsNoTracking()
            .Where(x => x.FeatureSnapshotId == featureSnapshotId);
        if (horizonTradingDays.HasValue)
            query = query.Where(x => x.HorizonTradingDays == horizonTradingDays);
        if (benchmarkSymbol is not null)
        {
            var normalized = benchmarkSymbol.Trim().ToUpperInvariant();
            query = query.Where(x => x.BenchmarkSymbol == normalized);
        }
        return await query.OrderBy(x => x.HorizonTradingDays).ThenBy(x => x.BenchmarkSymbol).ToListAsync();
    }
}
