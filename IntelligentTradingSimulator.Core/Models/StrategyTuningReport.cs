namespace IntelligentTradingSimulator.Core.Models;

public sealed record StrategyTuningRequest
{
    public int SchemaVersion { get; init; } = 1;
    public string BenchmarkSymbol { get; init; } = "SPY";
    public decimal MinimumReturnPercent { get; init; } = 0m;
    public decimal HighRiskDrawdownPercent { get; init; } = 20m;
    public IReadOnlyList<StrategyTuningRun> Runs { get; init; } = [];
}

public sealed record StrategyTuningRun
{
    public required BacktestSummaryReportResult Summary { get; init; }
    public StrategyConfiguration? Configuration { get; init; }
}

public sealed class StrategyTuningReport
{
    public int SchemaVersion { get; init; } = 1;
    public string Method { get; init; } = "heuristic-v1";
    public required string BenchmarkSymbol { get; init; }
    public decimal MinimumReturnPercent { get; init; }
    public decimal HighRiskDrawdownPercent { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public required IReadOnlyList<StrategyTuningAnalysis> Analyses { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } =
    [
        "Decision support only: no parameters are changed or deployed.",
        "Historical performance does not guarantee future returns. Validate candidates on held-out periods before further selection.",
        "Callers must supply authentic historical configurations and matching universes, costs, and data assumptions; these cannot be verified from aggregate metrics.",
        "Alpha is a simple return difference, not risk-adjusted alpha. Findings describe associations, not parameter causality."
    ];
}

public sealed record StrategyTuningAnalysis(
    int BacktestRunId,
    string StrategyId,
    StrategyComparisonReportEntry Metrics,
    string? ConfigurationJson,
    bool IsAnalyzable,
    bool IsHighPerforming,
    bool IsHighRisk,
    bool IsPromising,
    IReadOnlyList<string> Strengths,
    IReadOnlyList<string> Weaknesses,
    IReadOnlyList<string> Limitations);
