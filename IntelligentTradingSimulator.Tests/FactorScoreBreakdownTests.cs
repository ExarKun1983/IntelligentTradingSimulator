using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class FactorScoreBreakdownTests
{
    private static ScoringContext Context(
        decimal? rsi = 60m, decimal? distance = -25m, decimal? strength = 3m,
        bool aboveSma = true, decimal volatility = 1m) => new()
        {
            MomentumPercent = 2m,
            VolumeTrendPercent = 20m,
            VolatilityPercent = volatility,
            IsAboveSma5 = aboveSma,
            Rsi = rsi,
            DistanceFromWeek52HighPercent = distance,
            RelativeStrengthPercent = strength
        };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CalculateScore_ReportsAppliedFactorsInScoreOrderWithCustomWeights(bool legacyEngine)
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

        var score = engine.CalculateScore(Context(), out var strength, out var factors);

        Assert.NotNull(factors);
        Assert.Equal(new[] { "Momentum", "VolumeTrend", "Volatility", "SMA5", "RSI", "52WeekHigh", "RelativeStrength" },
            factors.Select(factor => factor.Factor));
        Assert.Equal(new decimal?[] { 4m, 2m, -3m, 5m, 4m, 4m, 6m },
            factors.Select(factor => factor.Contribution));
        Assert.All(factors, factor => Assert.Equal(FactorScoreStatus.Applied, factor.Status));
        Assert.Equal(22m, score);
        Assert.Equal(score, factors.Sum(factor => factor.Contribution ?? 0m));
        Assert.Equal(score, engine.CalculateScore(Context()));
        Assert.Equal(strength, factors.Last().Contribution);
        engine.CalculateScore(Context(), out _, out var repeated);
        Assert.Equal(factors, repeated);
    }

    [Theory]
    [InlineData(true, true, FactorScoreStatus.Applied)]
    [InlineData(false, true, FactorScoreStatus.Disabled)]
    [InlineData(true, false, FactorScoreStatus.Unavailable)]
    [InlineData(false, false, FactorScoreStatus.Unavailable)]
    public void CalculateScore_DistinguishesMissingDisabledAndNeutralOptionalFactors(
        bool enabled, bool available, FactorScoreStatus expectedStatus)
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            UseRsi = enabled, Use52WeekHigh = enabled, UseRelativeStrength = enabled
        });
        var context = Context(
            rsi: available ? 45m : null,
            distance: available ? -50m : null,
            strength: available ? 0m : null);

        var score = engine.CalculateScore(context, out _, out var factors);

        Assert.NotNull(factors);
        Assert.All(factors.Skip(4), factor =>
        {
            Assert.Equal(expectedStatus, factor.Status);
            Assert.Equal(available ? (decimal?)0m : null, factor.Contribution);
        });
        Assert.Equal(score, factors.Sum(factor => factor.Contribution ?? 0m));
    }

    [Fact]
    public void CalculateScore_ZeroWeightsReportDisabledWithoutDisablingSmaBonus()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            MomentumScoreWeight = 0m, VolumeTrendScoreWeight = 0m,
            VolatilityScoreWeight = 0m, UseRsi = false, Use52WeekHigh = true,
            Week52HighScoreWeight = 0m, RelativeStrengthScoreWeight = 0m
        });

        var score = engine.CalculateScore(Context(), out _, out var factors);

        Assert.NotNull(factors);
        Assert.All(factors.Where(factor => factor.Factor != "SMA5"), factor =>
        {
            Assert.Equal(FactorScoreStatus.Disabled, factor.Status);
            Assert.Equal(0m, factor.Contribution);
        });
        Assert.Equal(FactorScoreStatus.Applied, factors[3].Status);
        Assert.Equal(5m, score);
        Assert.Equal(score, factors.Sum(factor => factor.Contribution ?? 0m));
    }

    [Fact]
    public void CalculateScore_ReportsNegativeAndBoundaryOptionalContributions()
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            UseRsi = true, RsiOverboughtPenalty = 7m, Use52WeekHigh = true,
            Week52HighScoreWeight = 8m, RelativeStrengthScoreWeight = 2m
        });

        var score = engine.CalculateScore(Context(rsi: 80m, distance: -150m, strength: -3m),
            out var strength, out var factors);

        Assert.NotNull(factors);
        Assert.Equal(new decimal?[] { -7m, -8m, -6m },
            factors.Skip(4).Select(factor => factor.Contribution));
        Assert.Equal(-6m, strength);
        Assert.Equal(score, factors.Sum(factor => factor.Contribution ?? 0m));
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 4)]
    public void CalculateScore_DisqualificationOverrideReconcilesAndPreservesMissingStatus(
        bool aboveSma, bool qualityFilter, int volatility)
    {
        var engine = new CompositeScoringEngine(new StrategyConfiguration
        {
            UseQualityFilter = qualityFilter, UseRsi = true, Use52WeekHigh = true
        });

        var score = engine.CalculateScore(
            Context(rsi: null, aboveSma: aboveSma, volatility: volatility),
            out var strength, out var factors);

        Assert.NotNull(factors);
        Assert.Equal(qualityFilter ? 9 : 8, factors.Count);
        Assert.All(factors.Take(7).Where(factor => factor.Factor != "RSI"), factor =>
        {
            Assert.Equal(FactorScoreStatus.Disqualified, factor.Status);
            Assert.Equal(0m, factor.Contribution);
        });
        Assert.Equal(FactorScoreStatus.Unavailable, factors[4].Status);
        Assert.Null(factors[4].Contribution);
        Assert.Equal(new FactorScoreContribution(
            "Disqualification", StrategyConfiguration.DisqualifiedScore, FactorScoreStatus.Applied),
            factors.Last());
        Assert.Equal(0m, strength);
        Assert.Equal(StrategyConfiguration.DisqualifiedScore, score);
        Assert.Equal(score, factors.Sum(factor => factor.Contribution ?? 0m));
    }

    [Fact]
    public void Analyze_ExposesReconciledBreakdownForRankingAndBacktesting()
    {
        var configuration = new StrategyConfiguration
        {
            MomentumScoreWeight = 2m, UseRsi = true, Use52WeekHigh = true
        };
        var candles = Enumerable.Range(0, 5).Select(index => new MarketCandle
        {
            Date = new DateTime(2026, 1, 1).AddDays(index),
            Close = 100m + index, High = 101m + index, Low = 99m + index, Volume = 1000m
        }).ToList();

        var ranking = new MarketObservationAnalyzer(configuration).Analyze("TEST", candles);
        var backtest = new MarketObservationAnalyzerFactory().Create(configuration).Analyze("TEST", candles);

        Assert.NotNull(ranking.FactorContributions);
        Assert.Equal(7, ranking.FactorContributions.Count);
        Assert.Equal(ranking.Score, ranking.FactorContributions.Sum(factor => factor.Contribution ?? 0m));
        Assert.Equal(ranking.FactorContributions, backtest.FactorContributions);
        Assert.Equal(ranking.Score, backtest.Score);
        Assert.Equal(FactorScoreStatus.Unavailable, ranking.FactorContributions[4].Status);
        Assert.Equal(FactorScoreStatus.Unavailable, ranking.FactorContributions[5].Status);
        Assert.Equal(FactorScoreStatus.Unavailable, ranking.FactorContributions[6].Status);
    }

    [Fact]
    public void Analyze_CustomEngineWithoutBreakdownPreservesExistingDiagnostics()
    {
        var candles = new[]
        {
            new MarketCandle { Close = 100m, High = 101m, Low = 99m, Volume = 1000m },
            new MarketCandle { Close = 105m, High = 106m, Low = 104m, Volume = 1000m }
        };

        var result = new MarketObservationAnalyzer(new DiagnosticScoringEngine()).Analyze("TEST", candles);

        Assert.Equal(42m, result.Score);
        Assert.Equal(3m, result.RelativeStrengthScoreContribution);
        Assert.Null(result.FactorContributions);
    }

    private sealed class DiagnosticScoringEngine : IScoringEngine
    {
        public decimal CalculateScore(ScoringContext context) => 42m;

        public decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
        {
            relativeStrengthScoreContribution = 3m;
            return CalculateScore(context);
        }
    }
}
