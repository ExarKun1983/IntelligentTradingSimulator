namespace IntelligentTradingSimulator.Core.Models;

public sealed record ResultExplanation(
    string Summary,
    IReadOnlyList<string> Details,
    IReadOnlyList<string> Limitations);
