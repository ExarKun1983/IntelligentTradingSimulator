using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
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

        foreach (var snapshot in snapshots)
        {
            var exists = await dbContext.FeatureSnapshots.AnyAsync(x =>
                x.Symbol == snapshot.Symbol &&
                x.AnalysisDate == snapshot.AnalysisDate &&
                x.StrategyId == snapshot.StrategyId &&
                x.BacktestRunId == snapshot.BacktestRunId);
            if (!exists)
                dbContext.FeatureSnapshots.Add(snapshot);
        }

        await dbContext.SaveChangesAsync();
    }

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
