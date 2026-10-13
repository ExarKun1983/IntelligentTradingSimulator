using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class QualityFilterTests
{
    private static StrategyConfiguration Configuration() => new()
    {
        UseQualityFilter = true,
        QualityMinimumRevenueGrowthPercent = 5m,
        QualityMinimumEpsGrowthPercent = 3m,
        QualityMinimumMarginsPercent = 10m,
        QualityMaximumDebtToEquity = 2m
    };

    private static FundamentalData Fundamentals(
        decimal? revenue = 5m, decimal? eps = 3m, decimal? margins = 10m, decimal? debt = 2m) => new()
    {
        Symbol = "TEST", RevenueGrowthPercent = revenue, EpsGrowthPercent = eps,
        Margins = margins, Debt = debt
    };

    private static ScoringContext Context(FundamentalData? fundamentals, bool aboveSma = true) => new()
    {
        Symbol = "TEST", IsAboveSma5 = aboveSma, MomentumPercent = 2m,
        VolatilityPercent = 1m, Fundamentals = fundamentals
    };

    [Theory]
    [InlineData(5, 3, 10, 2)]
    [InlineData(6, 4, 11, 1)]
    public void Evaluate_PassesInclusiveThresholds(int revenue, int eps, int margins, int debt)
    {
        var result = new QualityFilter(Configuration()).Evaluate(Fundamentals(revenue, eps, margins, debt));
        Assert.False(result.IsExcluded);
        Assert.Equal(0m, result.Contribution);
        Assert.Contains("passed", result.Explanation);
    }

    [Theory]
    [InlineData(4, 3, 10, 2, "RevenueGrowthPercent")]
    [InlineData(5, 2, 10, 2, "EpsGrowthPercent")]
    [InlineData(5, 3, 9, 2, "Margins")]
    [InlineData(5, 3, 10, 3, "Debt")]
    [InlineData(-1, -1, -1, -1, "RevenueGrowthPercent")]
    public void Evaluate_ExcludesAnyFailedThresholdAndExplainsRule(
        int revenue, int eps, int margins, int debt, string metric)
    {
        var result = new QualityFilter(Configuration()).Evaluate(Fundamentals(revenue, eps, margins, debt));
        Assert.True(result.IsExcluded);
        Assert.Equal(0m, result.Contribution);
        Assert.Contains(metric, result.Explanation);
        Assert.Contains("Exclude", result.Explanation);
    }

    [Theory]
    [InlineData(QualityFilterAction.Neutral, false, 0)]
    [InlineData(QualityFilterAction.Penalize, false, -7)]
    [InlineData(QualityFilterAction.Exclude, true, 0)]
    public void Evaluate_UsesConfiguredFailurePolicyOnce(QualityFilterAction action, bool excluded, int points)
    {
        var configuration = Configuration();
        configuration.QualityFailureAction = action;
        configuration.QualityPenalty = 7m;
        var result = new QualityFilter(configuration).Evaluate(Fundamentals(0m, 0m, 0m, 3m));
        Assert.Equal(excluded, result.IsExcluded);
        Assert.Equal(points, result.Contribution);
    }

    [Theory]
    [InlineData(QualityFilterAction.Neutral, false, 0)]
    [InlineData(QualityFilterAction.Penalize, false, -5)]
    [InlineData(QualityFilterAction.Exclude, true, 0)]
    public void Evaluate_MissingSnapshotAndEachPartialFieldUseMissingPolicy(
        QualityFilterAction action, bool excluded, int points)
    {
        var configuration = Configuration();
        configuration.QualityMissingDataAction = action;
        foreach (var data in new[] { null, Fundamentals(revenue: null), Fundamentals(eps: null),
                     Fundamentals(margins: null), Fundamentals(debt: null), Fundamentals(debt: -1m) })
        {
            var result = new QualityFilter(configuration).Evaluate(data);
            Assert.Equal(excluded, result.IsExcluded);
            Assert.Equal(points, result.Contribution);
            Assert.Contains("Missing:", result.Explanation);
            Assert.DoesNotContain("Failed:", result.Explanation);
        }
    }

    [Theory]
    [InlineData(QualityFilterAction.Exclude, QualityFilterAction.Neutral, true, 0)]
    [InlineData(QualityFilterAction.Neutral, QualityFilterAction.Exclude, true, 0)]
    [InlineData(QualityFilterAction.Penalize, QualityFilterAction.Penalize, false, -5)]
    public void Evaluate_FailureAndMissingApplyMostRestrictiveActionWithoutDoublePenalty(
        QualityFilterAction failure, QualityFilterAction missing, bool excluded, int points)
    {
        var configuration = Configuration();
        configuration.QualityFailureAction = failure;
        configuration.QualityMissingDataAction = missing;
        var result = new QualityFilter(configuration).Evaluate(Fundamentals(revenue: 0m, eps: null));
        Assert.Equal(excluded, result.IsExcluded);
        Assert.Equal(points, result.Contribution);
        Assert.Contains("Failed:", result.Explanation);
        Assert.Contains("Missing:", result.Explanation);
    }

    [Fact]
    public void Evaluate_DisabledAndUnconfiguredMetricsDoNotAffectScores()
    {
        var configuration = Configuration();
        configuration.UseQualityFilter = false;
        Assert.Equal(new QualityFilterResult(false, 0m, "Disabled"),
            new QualityFilter(configuration).Evaluate(Fundamentals(0m, null, 0m, 3m)));

        configuration = new StrategyConfiguration
        {
            UseQualityFilter = true, QualityMinimumRevenueGrowthPercent = -5m,
            QualityMissingDataAction = QualityFilterAction.Exclude
        };
        Assert.False(new QualityFilter(configuration).Evaluate(Fundamentals(-5m, null, null, null)).IsExcluded);
        configuration.QualityMinimumRevenueGrowthPercent = null;
        Assert.False(new QualityFilter(configuration).Evaluate(null).IsExcluded);
    }

    [Theory]
    [InlineData(false, QualityFilterAction.Penalize, true, 19)]
    [InlineData(true, QualityFilterAction.Penalize, true, 19)]
    [InlineData(false, QualityFilterAction.Exclude, true, -999)]
    [InlineData(false, QualityFilterAction.Penalize, false, -999)]
    public void CalculateScore_QualityPointsReconcileAndDisqualificationClearsPenalty(
        bool legacy, QualityFilterAction action, bool aboveSma, int expected)
    {
        var configuration = Configuration();
        configuration.QualityFailureAction = action;
        IScoringEngine engine = legacy ? new RuleBasedScoringEngine(configuration) : new CompositeScoringEngine(configuration);
        var score = engine.CalculateScore(Context(Fundamentals(revenue: 0m), aboveSma), out _, out var factors);
        var quality = Assert.Single(factors!, f => f.Factor == "QualityFilter");
        Assert.Equal(expected, score);
        Assert.Equal(expected == -999 ? 0m : -5m, quality.Contribution);
        Assert.Contains("RevenueGrowthPercent=0 < 5", quality.Explanation);
        Assert.Equal(score, factors!.Sum(f => f.Contribution ?? 0m));
    }

    [Fact]
    public void CalculateScore_DisabledPreservesScoreAndZeroPenaltyIsNeutral()
    {
        var configuration = Configuration();
        configuration.UseQualityFilter = false;
        var engine = new CompositeScoringEngine(configuration);
        Assert.Equal(24m, engine.CalculateScore(Context(Fundamentals(revenue: 0m)), out _, out var factors));
        Assert.DoesNotContain(factors!, f => f.Factor == "QualityFilter");
        configuration.UseQualityFilter = true;
        configuration.QualityFailureAction = QualityFilterAction.Penalize;
        configuration.QualityPenalty = 0m;
        Assert.Equal(24m, engine.CalculateScore(Context(Fundamentals(revenue: 0m))));
    }

    [Theory]
    [InlineData(QualityFilterAction.Neutral, 13)]
    [InlineData(QualityFilterAction.Penalize, 8)]
    [InlineData(QualityFilterAction.Exclude, -999)]
    public void BacktestAnalyzer_AppliesConfiguredMissingDataPolicyWithoutLiveSnapshots(
        QualityFilterAction action, int expected)
    {
        var configuration = Configuration();
        configuration.MomentumScoreWeight = 2m;
        configuration.VolatilityScoreWeight = 0m;
        configuration.UseRelativeStrength = false;
        configuration.QualityMissingDataAction = action;
        var candles = Enumerable.Range(0, 5).Select(index => new MarketCandle
        {
            Date = new DateTime(2024, 1, 1).AddDays(index),
            Close = 100m + index, High = 101m + index, Low = 99m + index, Volume = 1000m
        }).ToList();

        var result = new MarketObservationAnalyzerFactory().Create(configuration).Analyze("TEST", candles);

        Assert.Equal(expected, result.Score);
        var factor = Assert.Single(result.FactorContributions!, f => f.Factor == "QualityFilter");
        Assert.Contains("Missing:", factor.Explanation);
        Assert.Equal(result.Score, result.FactorContributions!.Sum(f => f.Contribution ?? 0m));
    }

    [Theory]
    [InlineData("QualityPenalty", -1)]
    [InlineData("QualityMaximumDebtToEquity", -1)]
    [InlineData("QualityFailureAction", 99)]
    [InlineData("QualityMissingDataAction", 99)]
    public void CalculateScore_RejectsInvalidPolicyEvenWhenDisabled(string property, int value)
    {
        var configuration = new StrategyConfiguration();
        var info = typeof(StrategyConfiguration).GetProperty(property)!;
        info.SetValue(configuration, info.PropertyType == typeof(QualityFilterAction)
            ? (object)(QualityFilterAction)value : (decimal)value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CompositeScoringEngine(configuration).CalculateScore(Context(null)));
    }
}
