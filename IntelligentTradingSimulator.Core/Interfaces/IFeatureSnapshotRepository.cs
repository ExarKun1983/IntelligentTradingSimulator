using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IFeatureSnapshotRepository
{
    Task SaveAsync(
        IReadOnlyList<ObservationResult> observations,
        StrategyConfiguration strategyConfiguration,
        int? backtestRunId = null);

    Task<List<FeatureSnapshot>> QueryAsync(
        DateOnly from,
        DateOnly to,
        string? symbol = null,
        string? strategyId = null,
        int? backtestRunId = null);
}
