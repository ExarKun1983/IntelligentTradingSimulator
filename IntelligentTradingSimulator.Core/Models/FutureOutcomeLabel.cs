namespace IntelligentTradingSimulator.Core.Models;

public sealed class FutureOutcomeLabel
{
    public int Id { get; set; }
    public required int FeatureSnapshotId { get; init; }
    public required int HorizonTradingDays { get; init; }
    public required DateOnly OutcomeDate { get; init; }
    public required decimal ReturnPercent { get; init; }
    public required string BenchmarkSymbol { get; init; }
    public decimal? BenchmarkReturnPercent { get; init; }
    public bool? OutperformedBenchmark { get; init; }
}
