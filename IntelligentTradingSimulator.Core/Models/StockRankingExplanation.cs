namespace IntelligentTradingSimulator.Core.Models;

public sealed record StockRankingExplanation(
    string Symbol,
    decimal Score,
    int? Rank,
    string Summary,
    IReadOnlyList<RankingFactorExplanation> Factors,
    IReadOnlyList<string> Context,
    IReadOnlyList<string> Changes);

public sealed record RankingFactorExplanation(
    FactorScoreContribution Evidence,
    string Input,
    string Narrative);
