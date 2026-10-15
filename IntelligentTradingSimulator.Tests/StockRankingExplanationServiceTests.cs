using System.Globalization;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class StockRankingExplanationServiceTests
{
    private readonly StockRankingExplanationService _service = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explain_UsesActualInputsAndCustomWeightedContributions(bool legacyEngine)
    {
        var configuration = new StrategyConfiguration
        {
            MomentumScoreWeight = 2m, VolumeTrendScoreWeight = 0.1m,
            VolatilityScoreWeight = 3m, UseRsi = true, RsiPreferredRangeBonus = 4m,
            Use52WeekHigh = true, Week52HighScoreWeight = 8m,
            RelativeStrengthScoreWeight = 2m
        };
        IScoringEngine engine = legacyEngine
            ? new RuleBasedScoringEngine(configuration)
            : new CompositeScoringEngine(configuration);
        var observation = Score(engine, Features());

        var explanation = _service.Explain(observation, 3);

        Assert.Equal("TEST", explanation.Symbol);
        Assert.Equal(22m, explanation.Score);
        Assert.Equal(3, explanation.Rank);
        Assert.Contains("TEST ranked #3 has score 22 points.", explanation.Summary);
        Assert.Contains("Largest positive contribution: RelativeStrength (+6 points)", explanation.Summary);
        Assert.Contains("Largest negative contribution: Volatility (-3 points)", explanation.Summary);
        Assert.Equal(observation.FactorContributions, explanation.Factors.Select(factor => factor.Evidence));
        Assert.Equal(new[] { "2%", "20%", "1%", "above SMA5=True", "60",
            "distance from 52-week high=-25%", "RelativeStrength[QQQ]=3pp" },
            explanation.Factors.Select(factor => factor.Input));
        Assert.Contains("increased score by 4 points", explanation.Factors[0].Narrative);
        Assert.Contains("reduced score by 3 points", explanation.Factors[2].Narrative);
        Assert.Empty(explanation.Changes);
        Assert.Equal(22m, observation.Score);
    }

    [Fact]
    public void Explain_DistinguishesDisabledUnavailableAndNeutralWithoutGuessingCauses()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            MomentumScoreWeight = 0m, UseRsi = false, Use52WeekHigh = true
        });
        var observation = Score(engine, Features(rsi: null, strength: 0m));

        var explanation = _service.Explain(observation);

        Assert.Contains("disabled; no score contribution", explanation.Factors[0].Narrative);
        Assert.Contains("RSI: N/A; unavailable; no score contribution reported", explanation.Factors[4].Narrative);
        Assert.Contains("no score change (0 points)", explanation.Factors[6].Narrative);
        Assert.DoesNotContain("insufficient", string.Join(" ", explanation.Factors.Select(factor => factor.Narrative)));
        Assert.DoesNotContain("ranked", explanation.Summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explain_DisqualificationUsesOnlyScorerEvidence(bool qualityEnabled)
    {
        var observation = Score(new CompositeScoringEngine(new StrategyConfiguration
        {
            UseQualityFilter = qualityEnabled
        }), Features(aboveSma: false));

        var explanation = _service.Explain(observation, 10);

        Assert.Equal(-999m, explanation.Score);
        Assert.Contains("Largest negative contribution: Disqualification (-999 points)", explanation.Summary);
        Assert.Contains(explanation.Factors, factor =>
            factor.Narrative.Contains("not applied because the observation was disqualified"));
        Assert.Contains(explanation.Factors, factor =>
            factor.Narrative.Contains("disqualification override contributed -999 points"));
        if (qualityEnabled)
            Assert.Contains(explanation.Factors, factor => factor.Narrative.Contains("Not above SMA5"));
        else
            Assert.DoesNotContain(explanation.Factors, factor => factor.Narrative.Contains("Not above SMA5"));
    }

    [Fact]
    public void Explain_PreservesQualityPenaltyEvidenceAndLabelsRiskAndFundamentalContext()
    {
        var fundamentals = new FundamentalData
        {
            Symbol = "TEST", AsOfDate = new DateOnly(2025, 12, 31),
            RevenueGrowthPercent = 9m, Margins = 20m, Debt = 1.5m
        };
        var observation = Score(new CompositeScoringEngine(new StrategyConfiguration
        {
            UseQualityFilter = true, QualityMinimumRevenueGrowthPercent = 10m,
            QualityFailureAction = QualityFilterAction.Penalize, QualityPenalty = 7m
        }), Features(fundamentals: fundamentals));

        var explanation = _service.Explain(observation);
        var quality = Assert.Single(explanation.Factors, factor => factor.Evidence.Factor == "QualityFilter");

        Assert.Contains("reduced score by 7 points", quality.Narrative);
        Assert.Contains("Scorer evidence: Failed: RevenueGrowthPercent=9 < 10 (Penalize)", quality.Narrative);
        Assert.Contains(explanation.Context, text => text.Contains("ATR=2.5 price units (informational"));
        Assert.Contains(explanation.Context, text => text.Contains("RelativeStrength[QQQ]=3pp"));
        Assert.Contains(explanation.Context, text => text.Contains(
            "AsOf=2025-12-31, RevenueGrowth=9%, EPSGrowth=N/A, Margins=20%, DebtToEquity=1.5"));
        Assert.Contains(explanation.Context, text => text.Contains("only reported QualityFilter evidence"));
    }

    [Fact]
    public void Explain_UsesFeatureSnapshotEvenWhenLegacyFieldsDifferOrHaveOptionalValues()
    {
        var observation = Score(new CompositeScoringEngine(), Features(rsi: null));
        observation.MomentumPercent = 999m;
        observation.Rsi = 80m;
        observation.Atr = 999m;
        observation.Fundamentals = new FundamentalData { Symbol = "TEST", Debt = 999m };

        var explanation = _service.Explain(observation);

        Assert.Equal("2%", explanation.Factors[0].Input);
        Assert.Equal("N/A", explanation.Factors[4].Input);
        Assert.DoesNotContain(explanation.Context, text => text.Contains("999"));
        Assert.DoesNotContain(explanation.Context, text => text.Contains("Fundamental context"));
    }

    [Fact]
    public void Explain_LegacyInputsAndUnknownFutureFactorsDoNotRequireFeatureModel()
    {
        var observation = new ObservationResult
        {
            Symbol = "TEST", MomentumPercent = -2m, RelativeStrengthBenchmarkSymbol = "SPY",
            RelativeStrengthPercent = -3m, Score = -1m,
            FactorContributions =
            [
                new("Momentum", -2m, FactorScoreStatus.Applied),
                new("FutureFactor", 1m, FactorScoreStatus.Applied)
            ]
        };

        var explanation = _service.Explain(observation);

        Assert.Equal("-2%", explanation.Factors[0].Input);
        Assert.Equal("raw input not represented", explanation.Factors[1].Input);
        Assert.Contains("increased score by 1 points", explanation.Factors[1].Narrative);
        Assert.Contains(explanation.Context, text => text.Contains("RelativeStrength[SPY]=-3pp"));
        Assert.Contains(explanation.Context, text => text.Contains("ATR=N/A"));
        Assert.DoesNotContain(explanation.Context, text => text.Contains("N/App"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Explain_MissingBreakdownDoesNotInventRankingCauses(bool empty)
    {
        var observation = new ObservationResult
        {
            Symbol = "CUSTOM", Score = 42m, MomentumPercent = 100m,
            FactorContributions = empty ? [] : null
        };

        var explanation = _service.Explain(observation, 1);

        Assert.Contains("Factor breakdown unavailable; ranking causes cannot be determined", explanation.Summary);
        Assert.DoesNotContain("Largest", explanation.Summary);
        Assert.Empty(explanation.Factors);
    }

    [Fact]
    public void Explain_IncompleteBreakdownDoesNotClaimToExplainWholeScore()
    {
        var observation = Reported(42m, new FactorScoreContribution("Momentum", 1m, FactorScoreStatus.Applied));

        var explanation = _service.Explain(observation);

        Assert.Contains("do not reconcile", explanation.Summary);
        Assert.DoesNotContain("Largest", explanation.Summary);
        Assert.Single(explanation.Factors);
    }

    [Fact]
    public void Explain_ZeroContributionsAndTiedDriversAreDeterministic()
    {
        var zero = _service.Explain(Reported(0m, new FactorScoreContribution("Momentum", 0m, FactorScoreStatus.Applied)));
        var tied = _service.Explain(Reported(4m,
            new("Z", 2m, FactorScoreStatus.Applied), new("A", 2m, FactorScoreStatus.Applied)));

        Assert.Contains("All reported contributions are zero", zero.Summary);
        Assert.Contains("Largest positive contribution: A", tied.Summary);
    }

    [Fact]
    public void Explain_PreviousObservationReportsActualScoreAndContributionDrop()
    {
        var previous = Reported(8m, new("Momentum", 10m, FactorScoreStatus.Applied),
            new("Volatility", -2m, FactorScoreStatus.Applied));
        var current = Reported(3m, new("Momentum", 6m, FactorScoreStatus.Applied),
            new("Volatility", -3m, FactorScoreStatus.Applied));

        var explanation = _service.Explain(current, 6, previous);

        Assert.Contains("Score dropped from 8 to 3 points (change -5).", explanation.Changes);
        Assert.Contains("Momentum: contribution changed from 10 (Applied) to 6 (Applied) points (change -4).",
            explanation.Changes);
        Assert.Contains("Volatility: contribution changed from -2 (Applied) to -3 (Applied) points (change -1).",
            explanation.Changes);
        Assert.DoesNotContain(explanation.Changes, text => text.Contains("price") || text.Contains("entered"));
    }

    [Theory]
    [InlineData(2, "rose")]
    [InlineData(1, "was unchanged")]
    public void Explain_PreviousObservationReportsRisingAndUnchangedScores(int score, string direction)
    {
        var previous = Reported(1m, new FactorScoreContribution("Momentum", 1m, FactorScoreStatus.Applied));
        var current = Reported(score, new FactorScoreContribution("Momentum", score, FactorScoreStatus.Applied));

        var explanation = _service.Explain(current, previous: previous);

        Assert.StartsWith($"Score {direction}", explanation.Changes[0]);
    }

    [Fact]
    public void Explain_PreviousObservationPreservesStatusChangesAndPartialComparisonLimitations()
    {
        var previous = Reported(2m, new("Momentum", 2m, FactorScoreStatus.Applied),
            new("RSI", null, FactorScoreStatus.Unavailable));
        var current = Reported(1m, new("Momentum", 0m, FactorScoreStatus.Disabled),
            new("RSI", 1m, FactorScoreStatus.Applied), new("NewFactor", 0m, FactorScoreStatus.Applied));

        var explanation = _service.Explain(current, previous: previous);

        Assert.Contains(explanation.Changes, text => text.StartsWith("Factor sets changed"));
        Assert.Contains(explanation.Changes, text => text.Contains("2 (Applied) to 0 (Disabled)"));
        Assert.Contains("RSI: contribution comparison unavailable (Unavailable -> Applied).", explanation.Changes);
    }

    [Fact]
    public void Explain_PreviousObservationWithMissingOrDuplicateBreakdownDoesNotAttributeChanges()
    {
        var current = Reported(2m, new FactorScoreContribution("Momentum", 2m, FactorScoreStatus.Applied));
        var missing = new ObservationResult { Symbol = "TEST", Score = 4m };
        var duplicate = Reported(4m, new("Momentum", 2m, FactorScoreStatus.Applied),
            new("Momentum", 2m, FactorScoreStatus.Applied));

        foreach (var previous in new[] { missing, duplicate })
        {
            var explanation = _service.Explain(current, previous: previous);
            Assert.Equal(2, explanation.Changes.Count);
            Assert.StartsWith("Factor-level change attribution unavailable", explanation.Changes[1]);
        }
    }

    [Fact]
    public void Explain_UsesInvariantFullPrecisionRegardlessOfCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var observation = Reported(1.234567890123456789m,
                new FactorScoreContribution("Momentum", 1.234567890123456789m, FactorScoreStatus.Applied));

            var explanation = _service.Explain(observation);

            Assert.Contains("1.234567890123456789 points", explanation.Summary);
            Assert.Contains("1.234567890123456789 points", explanation.Factors[0].Narrative);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Explain_RejectsNullInvalidRanksDifferentSymbolsAndNonEarlierDates()
    {
        var current = Score(new CompositeScoringEngine(), Features());
        Assert.Throws<ArgumentNullException>(() => _service.Explain(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.Explain(current, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.Explain(current, -1));
        Assert.Throws<ArgumentException>(() => _service.Explain(current,
            previous: new ObservationResult { Symbol = "OTHER" }));
        Assert.Throws<ArgumentException>(() => _service.Explain(current, previous: current));
    }

    private static StockRankingFeatures Features(
        decimal? rsi = 60m, decimal? strength = 3m, bool aboveSma = true,
        FundamentalData? fundamentals = null) => new()
        {
            Symbol = "TEST", AnalysisDate = new DateOnly(2026, 1, 2),
            MomentumPercent = 2m, AverageVolume = 1000m, VolumeTrendPercent = 20m,
            VolatilityPercent = 1m, IsAboveSma5 = aboveSma, Rsi = rsi,
            DistanceFromWeek52HighPercent = -25m, RelativeStrengthPercent = strength,
            RelativeStrengthBenchmarkSymbol = "QQQ", Atr = 2.5m, Fundamentals = fundamentals
        };

    private static ObservationResult Score(IScoringEngine engine, StockRankingFeatures features)
    {
        var score = engine.CalculateScore(features.ToScoringContext(), out _, out var factors);
        return new ObservationResult
        {
            Symbol = features.Symbol, Features = features, Score = score, FactorContributions = factors
        };
    }

    private static ObservationResult Reported(decimal score, params FactorScoreContribution[] factors) => new()
    {
        Symbol = "TEST", Score = score, FactorContributions = factors
    };
}
