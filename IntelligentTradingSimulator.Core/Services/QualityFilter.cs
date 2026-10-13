using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class QualityFilter
{
    private readonly StrategyConfiguration _configuration;

    public QualityFilter(StrategyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!Enum.IsDefined(configuration.QualityFailureAction))
            throw new ArgumentOutOfRangeException(nameof(configuration.QualityFailureAction));
        if (!Enum.IsDefined(configuration.QualityMissingDataAction))
            throw new ArgumentOutOfRangeException(nameof(configuration.QualityMissingDataAction));
        if (configuration.QualityPenalty < 0m)
            throw new ArgumentOutOfRangeException(nameof(configuration.QualityPenalty));
        if (configuration.QualityMaximumDebtToEquity is < 0m)
            throw new ArgumentOutOfRangeException(nameof(configuration.QualityMaximumDebtToEquity));
        _configuration = configuration;
    }

    public QualityFilterResult Evaluate(FundamentalData? fundamentals)
    {
        if (!_configuration.UseQualityFilter)
            return new(false, 0m, "Disabled");

        var failures = new List<string>();
        var missing = new List<string>();
        void Check(string name, decimal? value, decimal? threshold, bool maximum = false)
        {
            if (!threshold.HasValue)
                return;
            if (!value.HasValue)
                missing.Add(name);
            else if (maximum && value.Value < 0m)
                missing.Add($"{name} (negative debt-to-equity is not usable)");
            else if (maximum ? value.Value > threshold.Value : value.Value < threshold.Value)
                failures.Add(FormattableString.Invariant(
                    $"{name}={value.Value} {(maximum ? ">" : "<")} {threshold.Value}"));
        }

        Check("RevenueGrowthPercent", fundamentals?.RevenueGrowthPercent, _configuration.QualityMinimumRevenueGrowthPercent);
        Check("EpsGrowthPercent", fundamentals?.EpsGrowthPercent, _configuration.QualityMinimumEpsGrowthPercent);
        Check("Margins", fundamentals?.Margins, _configuration.QualityMinimumMarginsPercent);
        Check("Debt", fundamentals?.Debt, _configuration.QualityMaximumDebtToEquity, maximum: true);

        var failureAction = failures.Count > 0 ? _configuration.QualityFailureAction : QualityFilterAction.Neutral;
        var missingAction = missing.Count > 0 ? _configuration.QualityMissingDataAction : QualityFilterAction.Neutral;
        // Exclusion takes precedence over a single penalty, which takes precedence over neutral.
        var action = (QualityFilterAction)Math.Max((int)failureAction, (int)missingAction);
        var reasons = new List<string>();
        if (failures.Count > 0)
            reasons.Add($"Failed: {string.Join(", ", failures)} ({failureAction})");
        if (missing.Count > 0)
            reasons.Add($"Missing: {string.Join(", ", missing)} ({missingAction})");
        if (reasons.Count == 0)
            reasons.Add(_configuration.QualityMinimumRevenueGrowthPercent is null &&
                _configuration.QualityMinimumEpsGrowthPercent is null &&
                _configuration.QualityMinimumMarginsPercent is null &&
                _configuration.QualityMaximumDebtToEquity is null
                    ? "No fundamental thresholds configured"
                    : "All configured thresholds passed (unconfigured metrics ignored)");

        return new(action == QualityFilterAction.Exclude,
            action == QualityFilterAction.Penalize ? -_configuration.QualityPenalty : 0m,
            string.Join("; ", reasons));
    }
}
