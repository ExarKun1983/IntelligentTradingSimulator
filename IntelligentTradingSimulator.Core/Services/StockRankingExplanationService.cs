using System.Globalization;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class StockRankingExplanationService : IStockRankingExplanationService
{
    public StockRankingExplanation Explain(
        ObservationResult observation,
        int? rank = null,
        ObservationResult? previous = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (rank is <= 0)
            throw new ArgumentOutOfRangeException(nameof(rank));
        if (previous is not null &&
            !string.Equals(previous.Symbol, observation.Symbol, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Previous observation must be for the same symbol.", nameof(previous));
        if (previous?.Features is not null && observation.Features is not null &&
            previous.Features.AnalysisDate >= observation.Features.AnalysisDate)
            throw new ArgumentException("Previous observation must have an earlier analysis date.", nameof(previous));

        var summary = $"{observation.Symbol}" +
            (rank.HasValue ? $" ranked #{rank.Value}" : "") +
            $" has score {Number(observation.Score)} points.";
        var factors = (observation.FactorContributions ?? [])
            .Select(factor => ExplainFactor(factor, Input(factor.Factor, observation)))
            .ToList();

        if (!Reconciles(observation))
        {
            summary += observation.FactorContributions is not { Count: > 0 }
                ? " Factor breakdown unavailable; ranking causes cannot be determined."
                : " Reported contributions do not reconcile to the score; a complete explanation is unavailable.";
        }
        else
        {
            var applied = observation.FactorContributions!
                .Where(factor => factor.Status == FactorScoreStatus.Applied);
            var positive = applied.Where(factor => factor.Contribution > 0m)
                .OrderByDescending(factor => factor.Contribution)
                .ThenBy(factor => factor.Factor, StringComparer.Ordinal).FirstOrDefault();
            var negative = applied.Where(factor => factor.Contribution < 0m)
                .OrderBy(factor => factor.Contribution)
                .ThenBy(factor => factor.Factor, StringComparer.Ordinal).FirstOrDefault();
            if (positive is not null)
                summary += $" Largest positive contribution: {positive.Factor} ({Points(positive.Contribution!.Value)} points).";
            if (negative is not null)
                summary += $" Largest negative contribution: {negative.Factor} ({Points(negative.Contribution!.Value)} points).";
            if (positive is null && negative is null)
                summary += " All reported contributions are zero.";
        }

        var features = observation.Features;
        var atr = features is null ? observation.Atr : features.Atr;
        var fundamentals = features is null ? observation.Fundamentals : features.Fundamentals;
        var context = new List<string>
        {
            $"Benchmark context: {Input("RelativeStrength", observation)} (no additional score effect inferred).",
            $"Risk context: ATR={Value(atr)} price units (informational; no additional score effect inferred)."
        };
        if (fundamentals is not null)
        {
            context.Add(
                $"Fundamental context: AsOf={fundamentals.AsOfDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "N/A"}, " +
                $"RevenueGrowth={Value(fundamentals.RevenueGrowthPercent, "%")}, EPSGrowth={Value(fundamentals.EpsGrowthPercent, "%")}, " +
                $"Margins={Value(fundamentals.Margins, "%")}, DebtToEquity={Value(fundamentals.Debt)} " +
                "(informational; only reported QualityFilter evidence establishes a scoring effect).");
        }

        return new(observation.Symbol, observation.Score, rank, summary,
            factors.AsReadOnly(), context.AsReadOnly(), Compare(observation, previous));
    }

    private static RankingFactorExplanation ExplainFactor(FactorScoreContribution factor, string input)
    {
        var effect = factor.Status switch
        {
            FactorScoreStatus.Disabled => "disabled; no score contribution",
            FactorScoreStatus.Unavailable => "unavailable; no score contribution reported",
            FactorScoreStatus.Disqualified => "not applied because the observation was disqualified",
            _ when factor.Contribution is null => "score contribution unavailable",
            _ when factor.Factor == "Disqualification" =>
                $"disqualification override contributed {Points(factor.Contribution.Value)} points; no further cause inferred",
            _ when factor.Contribution > 0m => $"increased score by {Number(factor.Contribution.Value)} points",
            _ when factor.Contribution < 0m => $"reduced score by {Number(-factor.Contribution.Value)} points",
            _ => "no score change (0 points)"
        };
        var narrative = $"{factor.Factor}: {input}; {effect}." +
            (string.IsNullOrWhiteSpace(factor.Explanation) ? "" : $" Scorer evidence: {factor.Explanation}");
        return new(factor, input, narrative);
    }

    private static IReadOnlyList<string> Compare(ObservationResult current, ObservationResult? previous)
    {
        var changes = new List<string>();
        if (previous is null)
            return changes.AsReadOnly();

        var delta = current.Score - previous.Score;
        changes.Add($"Score {(delta < 0m ? "dropped" : delta > 0m ? "rose" : "was unchanged")} " +
            $"from {Number(previous.Score)} to {Number(current.Score)} points (change {Points(delta)}).");
        var currentFactors = current.FactorContributions;
        var previousFactors = previous.FactorContributions;
        if (currentFactors is null || previousFactors is null ||
            !Reconciles(current) || !Reconciles(previous) ||
            currentFactors.Select(factor => factor.Factor).Distinct(StringComparer.Ordinal).Count() != currentFactors.Count ||
            previousFactors.Select(factor => factor.Factor).Distinct(StringComparer.Ordinal).Count() != previousFactors.Count)
        {
            changes.Add("Factor-level change attribution unavailable: complete, unique, reconciled breakdowns are required.");
            return changes.AsReadOnly();
        }

        var oldFactors = previousFactors.ToDictionary(factor => factor.Factor, StringComparer.Ordinal);
        if (!currentFactors.Select(factor => factor.Factor).ToHashSet(StringComparer.Ordinal)
            .SetEquals(oldFactors.Keys))
            changes.Add("Factor sets changed; only shared reported contributions can be compared.");

        foreach (var factor in currentFactors)
        {
            if (!oldFactors.TryGetValue(factor.Factor, out var old))
                continue;
            if (factor.Contribution is null || old.Contribution is null)
            {
                if (factor != old)
                    changes.Add($"{factor.Factor}: contribution comparison unavailable ({old.Status} -> {factor.Status}).");
                continue;
            }
            var change = factor.Contribution.Value - old.Contribution.Value;
            if (change != 0m || factor.Status != old.Status)
                changes.Add($"{factor.Factor}: contribution changed from {Number(old.Contribution.Value)} " +
                    $"({old.Status}) to {Number(factor.Contribution.Value)} ({factor.Status}) points " +
                    $"(change {Points(change)}).");
        }
        return changes.AsReadOnly();
    }

    private static bool Reconciles(ObservationResult observation) =>
        observation.FactorContributions is { Count: > 0 } factors &&
        factors.All(factor => factor.Status == FactorScoreStatus.Applied
            ? factor.Contribution.HasValue
            : factor.Contribution is null or 0m) &&
        factors.Sum(factor => factor.Contribution ?? 0m) == observation.Score;

    private static string Input(string factor, ObservationResult observation)
    {
        var features = observation.Features;
        return factor switch
        {
            "Momentum" => $"{Number(features is null ? observation.MomentumPercent : features.MomentumPercent)}%",
            "VolumeTrend" => $"{Number(features is null ? observation.VolumeTrendPercent : features.VolumeTrendPercent)}%",
            "Volatility" => $"{Number(features is null ? observation.VolatilityPercent : features.VolatilityPercent)}%",
            "SMA5" => $"above SMA5={(features is null ? observation.IsAboveSma5 : features.IsAboveSma5)}",
            "RSI" => Value(features is null ? observation.Rsi : features.Rsi),
            "52WeekHigh" => $"distance from 52-week high={Value(features is null ? observation.DistanceFromWeek52HighPercent : features.DistanceFromWeek52HighPercent, "%")}",
            "RelativeStrength" =>
                $"RelativeStrength[{(features is null ? observation.RelativeStrengthBenchmarkSymbol : features.RelativeStrengthBenchmarkSymbol) ?? "N/A"}]=" +
                $"{Value(features is null ? observation.RelativeStrengthPercent : features.RelativeStrengthPercent, "pp")}",
            _ => "raw input not represented"
        };
    }

    private static string Value(decimal? value, string unit = "") => value.HasValue ? Number(value.Value) + unit : "N/A";
    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private static string Points(decimal value) => (value > 0m ? "+" : "") + Number(value);
}
