namespace IntelligentTradingSimulator.Core.Models;

public enum QualityFilterAction
{
    Neutral,
    Penalize,
    Exclude
}

public sealed record QualityFilterResult(
    bool IsExcluded,
    decimal Contribution,
    string Explanation);
