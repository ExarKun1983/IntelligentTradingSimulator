namespace IntelligentTradingSimulator.Core.Models;

public sealed class StrategyConfiguration
{
    public const string DefaultId = "default";
    public const int DefaultMaxPositions = 10;
    public const decimal DefaultMaxPositionWeight = 100m;
    public const int DefaultLookbackDays = 90;
    public const decimal QualityVolatilityThresholdPercent = 3m;
    public const decimal DisqualifiedScore = -999m;
    public const decimal DefaultRsiPreferredMinimum = 50m;
    public const decimal DefaultRsiPreferredMaximum = 70m;
    public const decimal DefaultRsiOverboughtThreshold = 80m;
    public const decimal DefaultRsiPreferredRangeBonus = 5m;
    public const decimal DefaultRsiOverboughtPenalty = 5m;
    public const decimal DefaultWeek52HighScoreWeight = 5m;
    public const decimal DefaultRelativeStrengthScoreWeight = 1m;
    public const decimal DefaultMomentumScoreWeight = 10m;
    public const decimal DefaultVolumeTrendScoreWeight = 0.05m;
    public const decimal DefaultVolatilityScoreWeight = 1m;
    public const decimal DefaultSmaScoreWeight = 5m;
    public const decimal DefaultRsiScoreWeight = 1m;

    public string Id { get; set; } = DefaultId;

    public ScoringEngineType ScoringEngine { get; set; } = ScoringEngineType.Composite;

    public int MaxPositions { get; set; } = DefaultMaxPositions;

    public decimal MaxPositionWeight { get; set; } = DefaultMaxPositionWeight;

    /// <summary>Percentage loss from average entry price that exits a position; null disables Stop Loss.</summary>
    public decimal? StopLossPercent { get; set; }

    /// <summary>Percentage drawdown from the highest observed price since entry; null disables Trailing Stop.</summary>
    public decimal? TrailingStopPercent { get; set; }

    public bool UseHoldingConditionExit { get; set; } = false;

    public int LookbackDays { get; set; } = DefaultLookbackDays;

    /// <summary>Score points per percentage point of momentum; zero disables the contribution.</summary>
    public decimal MomentumScoreWeight { get; set; } = DefaultMomentumScoreWeight;

    /// <summary>Score points per percentage point of volume trend; zero disables the contribution.</summary>
    public decimal VolumeTrendScoreWeight { get; set; } = DefaultVolumeTrendScoreWeight;

    /// <summary>Penalty points per percentage point of volatility; zero disables the contribution, not the quality filter.</summary>
    public decimal VolatilityScoreWeight { get; set; } = DefaultVolatilityScoreWeight;

    /// <summary>Score points for SMA5 eligibility; zero disables the bonus, not the eligibility filter.</summary>
    public decimal SmaScoreWeight { get; set; } = DefaultSmaScoreWeight;

    public bool UseRsi { get; set; } = false;

    /// <summary>Non-negative multiplier applied to both RSI threshold-based bonus and penalty points.</summary>
    public decimal RsiScoreWeight { get; set; } = DefaultRsiScoreWeight;

    public decimal RsiPreferredMinimum { get; set; } = DefaultRsiPreferredMinimum;

    public decimal RsiPreferredMaximum { get; set; } = DefaultRsiPreferredMaximum;

    public decimal RsiOverboughtThreshold { get; set; } = DefaultRsiOverboughtThreshold;

    public decimal RsiPreferredRangeBonus { get; set; } = DefaultRsiPreferredRangeBonus;

    public decimal RsiOverboughtPenalty { get; set; } = DefaultRsiOverboughtPenalty;

    public bool UseRelativeStrength { get; set; } = true;

    public string RelativeStrengthBenchmarkSymbol { get; set; } = "SPY";

    /// <summary>
    /// Score points per percentage point of benchmark-relative outperformance.
    /// Negative relative strength subtracts points when <see cref="UseRelativeStrength"/> is enabled.
    /// </summary>
    public decimal RelativeStrengthScoreWeight { get; set; } =
        DefaultRelativeStrengthScoreWeight;

    public bool Use52WeekHigh { get; set; } = false;

    /// <summary>
    /// Maximum absolute score contribution from the 52-week-high distance factor.
    /// A 0% distance contributes +weight, -50% contributes 0, and -100% or worse
    /// contributes -weight when <see cref="Use52WeekHigh"/> is enabled.
    /// </summary>
    public decimal Week52HighScoreWeight { get; set; } =
        DefaultWeek52HighScoreWeight;

    public bool UseQualityFilter { get; set; } = false;

    /// <summary>Null thresholds disable the corresponding fundamental checks; percentages use percentage points.</summary>
    public decimal? QualityMinimumRevenueGrowthPercent { get; set; }
    public decimal? QualityMinimumEpsGrowthPercent { get; set; }
    public decimal? QualityMinimumMarginsPercent { get; set; }
    public decimal? QualityMaximumDebtToEquity { get; set; }

    public QualityFilterAction QualityFailureAction { get; set; } = QualityFilterAction.Exclude;
    public QualityFilterAction QualityMissingDataAction { get; set; } = QualityFilterAction.Neutral;

    /// <summary>Non-negative score points subtracted once when the selected quality action is Penalize.</summary>
    public decimal QualityPenalty { get; set; } = 5m;
}
