using System;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record WorkflowProgressContext
{
    public string? Workflow { get; init; }
    public string? StrategyId { get; init; }
    public int? RunId { get; init; }
    public DateTime? Date { get; init; }
    public int? CompletedDates { get; init; }
    public int? TotalDates { get; init; }
    public int? CompletedSymbols { get; init; }
    public int? TotalSymbols { get; init; }
    public int? VariantIndex { get; init; }
    public int? TotalVariants { get; init; }
}
