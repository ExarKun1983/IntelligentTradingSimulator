using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class FeatureSnapshotRepository(
    TradingSimulatorDbContext dbContext) : IFeatureSnapshotRepository
{
    public async Task SaveAsync(
        IReadOnlyList<ObservationResult> observations,
        StrategyConfiguration strategyConfiguration,
        int? backtestRunId = null)
    {
        var snapshots = observations.Select(observation =>
        {
            var features = observation.Features
                ?? throw new ArgumentException("Observations must contain dated ranking features.", nameof(observations));
            if (features.Symbol != observation.Symbol)
                throw new ArgumentException("Feature and observation symbols must match.", nameof(observations));
            if (features.Fundamentals?.AsOfDate > features.AnalysisDate)
                throw new ArgumentException("Fundamentals must not be dated after the analysis date.", nameof(observations));

            return new FeatureSnapshot
            {
                Symbol = features.Symbol.Trim().ToUpperInvariant(),
                AnalysisDate = features.AnalysisDate,
                StrategyId = strategyConfiguration.Id,
                BacktestRunId = backtestRunId,
                Features = features,
                Score = observation.Score,
                StrategyConfigurationJson = JsonSerializer.Serialize(strategyConfiguration),
                FactorContributionsJson = observation.FactorContributions is null
                    ? null
                    : JsonSerializer.Serialize(observation.FactorContributions)
            };
        }).DistinctBy(x => (x.Symbol, x.AnalysisDate, x.StrategyId, x.BacktestRunId)).ToList();

        if (snapshots.Count > 0)
        {
            var symbols = snapshots.Select(x => x.Symbol).Distinct().ToArray();
            var from = snapshots.Min(x => x.AnalysisDate);
            var to = snapshots.Max(x => x.AnalysisDate);
            var existingKeys = await dbContext.FeatureSnapshots
                .Where(x => x.StrategyId == strategyConfiguration.Id &&
                    x.BacktestRunId == backtestRunId &&
                    x.AnalysisDate >= from && x.AnalysisDate <= to &&
                    symbols.Contains(x.Symbol))
                .Select(x => new { x.Symbol, x.AnalysisDate })
                .ToListAsync();
            var existing = existingKeys
                .Select(x => (x.Symbol, x.AnalysisDate))
                .ToHashSet();

            var snapshotsToInsert = snapshots.Where(snapshot =>
                !existing.Contains((snapshot.Symbol, snapshot.AnalysisDate))).ToList();
            var pendingSnapshots = snapshotsToInsert;
            while (pendingSnapshots.Count > 0)
            {
                dbContext.FeatureSnapshots.AddRange(pendingSnapshots);
                try
                {
                    await dbContext.SaveChangesAsync();
                    break;
                }
                catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
                {
                    foreach (var snapshot in pendingSnapshots)
                        dbContext.Entry(snapshot).State = EntityState.Detached;

                    var persistedKeys = await dbContext.FeatureSnapshots
                        .AsNoTracking()
                        .Where(x => x.StrategyId == strategyConfiguration.Id &&
                            x.BacktestRunId == backtestRunId &&
                            x.AnalysisDate >= from && x.AnalysisDate <= to &&
                            symbols.Contains(x.Symbol))
                        .Select(x => new { x.Symbol, x.AnalysisDate })
                        .ToListAsync();
                    var persisted = persistedKeys
                        .Select(x => (x.Symbol, x.AnalysisDate))
                        .ToHashSet();
                    var remainingSnapshots = pendingSnapshots
                        .Where(snapshot => !persisted.Contains((snapshot.Symbol, snapshot.AnalysisDate)))
                        .ToList();

                    if (remainingSnapshots.Count == pendingSnapshots.Count)
                        throw;

                    foreach (var snapshot in remainingSnapshots)
                        snapshot.Id = 0;
                    pendingSnapshots = remainingSnapshots;
                }
            }
        }
        else
            await dbContext.SaveChangesAsync();
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.GetBaseException() is SqlException { Number: 2601 or 2627 };

    public async Task<List<FeatureSnapshot>> QueryAsync(
        DateOnly from,
        DateOnly to,
        string? symbol = null,
        string? strategyId = null,
        int? backtestRunId = null)
    {
        var query = dbContext.FeatureSnapshots.AsNoTracking()
            .Where(x => x.AnalysisDate >= from && x.AnalysisDate <= to);
        if (symbol is not null)
        {
            var normalizedSymbol = symbol.Trim().ToUpperInvariant();
            query = query.Where(x => x.Symbol == normalizedSymbol);
        }
        if (strategyId is not null)
            query = query.Where(x => x.StrategyId == strategyId);
        if (backtestRunId.HasValue)
            query = query.Where(x => x.BacktestRunId == backtestRunId);

        return await query.OrderBy(x => x.AnalysisDate).ThenBy(x => x.Symbol)
            .ThenBy(x => x.StrategyId).ThenBy(x => x.BacktestRunId).ToListAsync();
    }
}
