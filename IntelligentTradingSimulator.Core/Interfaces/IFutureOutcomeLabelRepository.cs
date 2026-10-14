using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IFutureOutcomeLabelRepository
{
    Task SaveAsync(IReadOnlyList<FutureOutcomeLabel> labels);

    Task<List<FutureOutcomeLabel>> QueryAsync(
        int featureSnapshotId, int? horizonTradingDays = null, string? benchmarkSymbol = null);
}
