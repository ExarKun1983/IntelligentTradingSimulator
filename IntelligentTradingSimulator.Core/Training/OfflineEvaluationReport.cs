namespace IntelligentTradingSimulator.Core.Training;

public sealed record OfflineEvaluationRequest
{
    public required string ModelId { get; init; }
    public required DateOnly TrainingCutoff { get; init; }
    public required DateOnly OutcomeCutoff { get; init; }
    public int TopCount { get; init; } = 10;
}

public sealed class OfflineEvaluationReport
{
    public int SchemaVersion { get; init; } = 1;
    public required TrainingDatasetQuery Query { get; init; }
    public required OfflineEvaluationRequest Request { get; init; }
    public string BaselineName { get; init; } = "Composite";
    public required string BaselineConfigurationJson { get; init; }
    public required int ExcludedRowCount { get; init; }
    public required OfflineEvaluationMetrics Model { get; init; }
    public required OfflineEvaluationMetrics Baseline { get; init; }
    public required IReadOnlyList<OfflineEvaluationRow> Rows { get; init; }
}

public sealed record OfflineEvaluationMetrics(
    int SampleCount,
    decimal? DirectionalAccuracyPercent,
    double? MeanRankCorrelation,
    int RankCorrelationGroupCount,
    decimal? AverageTopForwardReturnPercent,
    int TopSelectionCount);

public sealed record OfflineEvaluationRow(
    int FeatureSnapshotId,
    int FutureOutcomeLabelId,
    string Symbol,
    DateOnly AnalysisDate,
    int? BacktestRunId,
    DateOnly OutcomeDate,
    decimal ReturnPercent,
    decimal ModelScore,
    decimal BaselineScore);
