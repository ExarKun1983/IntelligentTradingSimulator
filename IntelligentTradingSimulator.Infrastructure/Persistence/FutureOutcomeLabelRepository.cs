using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class FutureOutcomeLabelRepository(
    TradingSimulatorDbContext dbContext) : IFutureOutcomeLabelRepository
{
    public async Task SaveAsync(IReadOnlyList<FutureOutcomeLabel> labels)
    {
        foreach (var label in labels.DistinctBy(x =>
                     (x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol)))
        {
            var exists = await dbContext.FutureOutcomeLabels.AnyAsync(x =>
                x.FeatureSnapshotId == label.FeatureSnapshotId &&
                x.HorizonTradingDays == label.HorizonTradingDays &&
                x.BenchmarkSymbol == label.BenchmarkSymbol);
            if (!exists)
                dbContext.FutureOutcomeLabels.Add(label);
        }
        await dbContext.SaveChangesAsync();
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
