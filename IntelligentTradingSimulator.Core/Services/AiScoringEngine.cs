using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

/// <summary>
/// Non-production AI integration boundary. By default, delegates to deterministic
/// composite scoring; a model adapter can replace it without changing ranking.
/// Input is <see cref="ScoringContext"/>, including its optional dated feature row.
/// Output is a decimal ranking score (or the disqualification sentinel) and optional
/// applied diagnostics. Missing model diagnostics must remain null.
/// </summary>
public sealed class AiScoringEngine : IScoringEngine
{
    private readonly IScoringEngine _scoringAdapter;

    public AiScoringEngine(
        StrategyConfiguration? strategyConfiguration = null,
        IScoringEngine? scoringAdapter = null)
    {
        _scoringAdapter = scoringAdapter ?? new CompositeScoringEngine(strategyConfiguration);
    }

    public decimal CalculateScore(ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _scoringAdapter.CalculateScore(context);
    }

    public decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _scoringAdapter.CalculateScore(context, out relativeStrengthScoreContribution);
    }

    public decimal CalculateScore(
        ScoringContext context,
        out decimal? relativeStrengthScoreContribution,
        out IReadOnlyList<FactorScoreContribution>? factorContributions)
    {
        ArgumentNullException.ThrowIfNull(context);
        return _scoringAdapter.CalculateScore(
            context, out relativeStrengthScoreContribution, out factorContributions);
    }
}
