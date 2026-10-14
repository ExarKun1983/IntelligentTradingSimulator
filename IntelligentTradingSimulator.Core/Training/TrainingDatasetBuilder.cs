using IntelligentTradingSimulator.Core.Interfaces;

namespace IntelligentTradingSimulator.Core.Training;

/// <summary>Assembles stored features and labels for offline use only; never generates future outcomes.</summary>
public sealed class TrainingDatasetBuilder(
    IFeatureSnapshotRepository snapshots,
    IFutureOutcomeLabelRepository labels)
{
    public async Task<TrainingDataset> BuildAsync(TrainingDatasetQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.From > query.To)
            throw new ArgumentException("The start date must not be after the end date.", nameof(query));
        if (query.HorizonTradingDays is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "The label horizon must be positive.");
        if (query.BacktestRunId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(query), "The backtest run ID must be positive.");
        if (query.Symbol is not null && string.IsNullOrWhiteSpace(query.Symbol) ||
            query.StrategyId is not null && string.IsNullOrWhiteSpace(query.StrategyId) ||
            query.BenchmarkSymbol is not null && string.IsNullOrWhiteSpace(query.BenchmarkSymbol))
            throw new ArgumentException("Context filters must not be blank.", nameof(query));

        var normalizedQuery = query with
        {
            Symbol = query.Symbol?.Trim().ToUpperInvariant(),
            BenchmarkSymbol = query.BenchmarkSymbol?.Trim().ToUpperInvariant()
        };
        var selectedSnapshots = await snapshots.QueryAsync(
            normalizedQuery.From, normalizedQuery.To, normalizedQuery.Symbol,
            normalizedQuery.StrategyId, normalizedQuery.BacktestRunId);
        var rows = new List<TrainingDatasetRow>();
        foreach (var snapshot in selectedSnapshots
                     .OrderBy(x => x.AnalysisDate)
                     .ThenBy(x => x.Symbol, StringComparer.Ordinal)
                     .ThenBy(x => x.StrategyId, StringComparer.Ordinal)
                     .ThenBy(x => x.BacktestRunId)
                     .ThenBy(x => x.Id))
        {
            var selectedLabels = await labels.QueryAsync(
                snapshot.Id, normalizedQuery.HorizonTradingDays, normalizedQuery.BenchmarkSymbol);
            foreach (var label in selectedLabels.OrderBy(x => x.HorizonTradingDays)
                         .ThenBy(x => x.BenchmarkSymbol, StringComparer.Ordinal))
            {
                rows.Add(new TrainingDatasetRow
                {
                    FeatureSnapshotId = snapshot.Id,
                    FeatureSchemaVersion = snapshot.SchemaVersion,
                    FutureOutcomeLabelId = label.Id,
                    Symbol = snapshot.Symbol,
                    AnalysisDate = snapshot.AnalysisDate,
                    StrategyId = snapshot.StrategyId,
                    BacktestRunId = snapshot.BacktestRunId,
                    StrategyConfigurationJson = normalizedQuery.IncludeStrategyMetadata
                        ? snapshot.StrategyConfigurationJson : null,
                    Features = snapshot.Features,
                    HorizonTradingDays = label.HorizonTradingDays,
                    OutcomeDate = label.OutcomeDate,
                    ReturnPercent = label.ReturnPercent,
                    BenchmarkSymbol = label.BenchmarkSymbol,
                    BenchmarkReturnPercent = label.BenchmarkReturnPercent,
                    RelativeReturnPercent = label.BenchmarkReturnPercent.HasValue
                        ? label.ReturnPercent - label.BenchmarkReturnPercent.Value : null,
                    OutperformedBenchmark = label.OutperformedBenchmark
                });
            }
        }

        return new TrainingDataset { Query = normalizedQuery, Rows = rows };
    }
}
