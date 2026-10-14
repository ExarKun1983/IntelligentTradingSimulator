using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Training;

public sealed record TrainingDatasetQuery
{
    public DateOnly From { get; init; } = DateOnly.MinValue;
    public DateOnly To { get; init; } = DateOnly.MaxValue;
    public string? Symbol { get; init; }
    public string? StrategyId { get; init; }
    public int? BacktestRunId { get; init; }
    public int? HorizonTradingDays { get; init; }
    public string? BenchmarkSymbol { get; init; }
    public bool IncludeStrategyMetadata { get; init; }
}

public sealed class TrainingDataset
{
    public int SchemaVersion { get; init; } = 1;
    public required TrainingDatasetQuery Query { get; init; }
    public required IReadOnlyList<TrainingDatasetRow> Rows { get; init; }
}

public sealed class TrainingDatasetRow
{
    public required int FeatureSnapshotId { get; init; }
    public required int FeatureSchemaVersion { get; init; }
    public required int FutureOutcomeLabelId { get; init; }
    public required string Symbol { get; init; }
    public required DateOnly AnalysisDate { get; init; }
    public required string StrategyId { get; init; }
    public int? BacktestRunId { get; init; }
    public string? StrategyConfigurationJson { get; init; }
    public required StockRankingFeatures Features { get; init; }
    public required int HorizonTradingDays { get; init; }
    public required DateOnly OutcomeDate { get; init; }
    public required decimal ReturnPercent { get; init; }
    public required string BenchmarkSymbol { get; init; }
    public decimal? BenchmarkReturnPercent { get; init; }
    public decimal? RelativeReturnPercent { get; init; }
    public bool? OutperformedBenchmark { get; init; }
}
