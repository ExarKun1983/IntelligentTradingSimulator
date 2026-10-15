using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class AiScoringEngineTests
{
    [Theory]
    [InlineData(false, false, false, 5)]
    [InlineData(true, true, true, 5)]
    [InlineData(true, true, true, -5)]
    [InlineData(true, false, true, 5)]
    public void CalculateScore_PlaceholderPreservesConfiguredCompositeScoreAndDiagnostics(
        bool enabled, bool aboveSma, bool optionalAvailable, int momentum)
    {
        var strategy = new StrategyConfiguration
        {
            UseRsi = enabled,
            UseRelativeStrength = enabled,
            Use52WeekHigh = enabled,
            UseQualityFilter = enabled,
            MomentumScoreWeight = 2m,
            VolumeTrendScoreWeight = 0.2m,
            VolatilityScoreWeight = 3m,
            SmaScoreWeight = 4m,
            RsiScoreWeight = 2m,
            RelativeStrengthScoreWeight = 3m,
            Week52HighScoreWeight = 7m
        };
        var context = new ScoringContext
        {
            Symbol = "TEST",
            MomentumPercent = momentum,
            AverageVolume = 1_000_000m,
            VolumeTrendPercent = -10m,
            VolatilityPercent = 1m,
            IsAboveSma5 = aboveSma,
            Rsi = optionalAvailable ? 60m : null,
            RelativeStrengthPercent = optionalAvailable ? -2m : null,
            DistanceFromWeek52HighPercent = optionalAvailable ? -20m : null
        };
        var expected = new CompositeScoringEngine(strategy).CalculateScore(
            context, out var expectedRelativeStrength, out var expectedFactors);
        IScoringEngine engine = new AiScoringEngine(strategy);

        Assert.Equal(expected, engine.CalculateScore(context));
        Assert.Equal(expected, engine.CalculateScore(context, out var relativeStrength));
        Assert.Equal(expectedRelativeStrength, relativeStrength);
        Assert.Equal(expected, engine.CalculateScore(context, out relativeStrength, out var factors));
        Assert.Equal(expectedRelativeStrength, relativeStrength);
        Assert.Equal(expectedFactors, factors);
        Assert.Equal(expected, factors!.Sum(factor => factor.Contribution ?? 0m));
        Assert.Equal(expected, engine.CalculateScore(context));
        if (enabled && !aboveSma)
            Assert.Equal(StrategyConfiguration.DisqualifiedScore, expected);
    }

    [Fact]
    public void CalculateScore_DefaultPlaceholderMatchesDefaultComposite()
    {
        var context = new ScoringContext { MomentumPercent = 2m, IsAboveSma5 = true };

        Assert.Equal(new CompositeScoringEngine().CalculateScore(context),
            new AiScoringEngine().CalculateScore(context));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-12)]
    [InlineData(-999)]
    public void Analyze_ReplacementAdapterReceivesCompleteFeatures_WithoutInventingDiagnostics(int score)
    {
        var adapter = new CapturingAdapter(score);
        var analyzer = new MarketObservationAnalyzer(new AiScoringEngine(scoringAdapter: adapter));
        var candles = Enumerable.Range(0, 5).Select(index => new MarketCandle
        {
            Date = new DateTime(2024, 1, 1).AddDays(index),
            Open = 100m + index,
            Close = 100m + index,
            High = 101m + index,
            Low = 99m + index,
            Volume = 1_000_000m
        }).ToArray();

        var result = analyzer.Analyze("TEST", candles);

        Assert.Equal(score, result.Score);
        Assert.Equal(1, adapter.Calls);
        Assert.NotNull(result.Features);
        Assert.Same(result.Features, adapter.Context!.Features);
        Assert.Equal("TEST", adapter.Context.Symbol);
        Assert.Equal(new DateOnly(2024, 1, 5), adapter.Context.Features!.AnalysisDate);
        Assert.Null(adapter.Context.Features.Rsi);
        Assert.Null(adapter.Context.Features.Atr);
        Assert.Null(adapter.Context.Features.RelativeStrengthPercent);
        Assert.Null(result.RelativeStrengthScoreContribution);
        Assert.Null(result.FactorContributions);
    }

    [Fact]
    public void CalculateScore_ReplacementAdapterPreservesDiagnosticsAndLegacyInput()
    {
        var context = new ScoringContext { Symbol = "TEST", MomentumPercent = 5m };
        IScoringEngine engine = new AiScoringEngine(scoringAdapter: new DiagnosticAdapter());

        Assert.Equal(5m, engine.CalculateScore(context));
        Assert.Equal(5m, engine.CalculateScore(context, out var relativeStrength));
        Assert.Equal(2m, relativeStrength);
        Assert.Equal(5m, engine.CalculateScore(context, out relativeStrength, out var factors));
        Assert.Equal(2m, relativeStrength);
        Assert.Same(DiagnosticAdapter.Factors, factors);
    }

    [Fact]
    public void CalculateScore_AdapterFailurePropagatesWithoutFallback()
    {
        IScoringEngine engine = new AiScoringEngine(scoringAdapter: new FailingAdapter());
        var context = new ScoringContext();

        Assert.Throws<InvalidOperationException>(() => engine.CalculateScore(context));
        Assert.Throws<InvalidOperationException>(() => engine.CalculateScore(context, out _));
        Assert.Throws<InvalidOperationException>(() => engine.CalculateScore(context, out _, out _));
    }

    [Fact]
    public void CalculateScore_RejectsNullContext_ForEveryOverload()
    {
        IScoringEngine engine = new AiScoringEngine();

        Assert.Throws<ArgumentNullException>(() => engine.CalculateScore(null!));
        Assert.Throws<ArgumentNullException>(() => engine.CalculateScore(null!, out _));
        Assert.Throws<ArgumentNullException>(() => engine.CalculateScore(null!, out _, out _));
    }

    private sealed class CapturingAdapter(decimal score) : IScoringEngine
    {
        public ScoringContext? Context { get; private set; }
        public int Calls { get; private set; }

        public decimal CalculateScore(ScoringContext context)
        {
            Context = context;
            Calls++;
            return score;
        }
    }

    private sealed class DiagnosticAdapter : IScoringEngine
    {
        public static readonly IReadOnlyList<FactorScoreContribution> Factors = [];

        public decimal CalculateScore(ScoringContext context) => context.MomentumPercent;

        public decimal CalculateScore(ScoringContext context, out decimal? relativeStrengthScoreContribution)
        {
            relativeStrengthScoreContribution = 2m;
            return CalculateScore(context);
        }

        public decimal CalculateScore(
            ScoringContext context,
            out decimal? relativeStrengthScoreContribution,
            out IReadOnlyList<FactorScoreContribution>? factorContributions)
        {
            factorContributions = Factors;
            return CalculateScore(context, out relativeStrengthScoreContribution);
        }
    }

    private sealed class FailingAdapter : IScoringEngine
    {
        public decimal CalculateScore(ScoringContext context) =>
            throw new InvalidOperationException("Model unavailable.");
    }
}
