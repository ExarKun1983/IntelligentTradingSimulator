using System.Text.Json;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public class StrategyTuningAnalysisServiceTests
{
    private readonly StrategyTuningAnalysisService _service = new(new StrategyComparisonReportService());

    [Fact]
    public void Analyze_IdentifiesPromisingAndHighRiskParameterSets_WithoutChangingParameters()
    {
        var safe = Run(1, "safe", 15m, 5m);
        var risky = Run(2, "risky", 30m, 25m);
        risky.Configuration!.MomentumScoreWeight = 50m;
        var request = new StrategyTuningRequest { Runs = [safe, risky] };
        var before = JsonSerializer.Serialize(request);

        var report = _service.Analyze(request);

        Assert.Equal(before, JsonSerializer.Serialize(request));
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("heuristic-v1", report.Method);
        Assert.Equal(2, report.Analyses.Single(analysis => analysis.Metrics.Rank == 1).BacktestRunId);
        Assert.Equal(1, report.Analyses[1].Metrics.Rank);
        Assert.Equal(30m, report.Analyses[1].Metrics.ReturnPercent);
        Assert.Equal(safe.Summary.StartDate, report.StartDate);
        Assert.Equal(safe.Summary.EndDate, report.EndDate);
        Assert.True(report.Analyses[0].IsPromising);
        Assert.True(report.Analyses[1].IsHighPerforming);
        Assert.True(report.Analyses[1].IsHighRisk);
        Assert.False(report.Analyses[1].IsPromising);
        Assert.Contains(report.Analyses[0].Strengths, text => text.Contains("Outperformed SPY"));
        Assert.Contains(report.Analyses[1].Weaknesses, text => text.Contains("High risk"));
        Assert.Equal(50m, JsonSerializer.Deserialize<StrategyConfiguration>(
            report.Analyses[1].ConfigurationJson!)!.MomentumScoreWeight);
        Assert.Contains(report.Limitations, text => text.Contains("Decision support only"));
        Assert.Contains(report.Limitations, text => text.Contains("held-out"));
    }

    [Theory]
    [InlineData(20, 20, 0, true, true, false)]
    [InlineData(20, 21, 0, false, true, true)]
    [InlineData(20, 21, 20, false, false, false)]
    [InlineData(0, 21, 0, false, false, false)]
    [InlineData(-5, 21, 0, false, false, false)]
    public void Analyze_RespectsConfiguredThresholdBoundaries(
        int returns, int riskThreshold, int returnThreshold, bool highRisk, bool highPerforming, bool promising)
    {
        var report = _service.Analyze(new StrategyTuningRequest
        {
            Runs = [Run(1, "test", returns, 20m), Run(2, "other")],
            MinimumReturnPercent = returnThreshold,
            HighRiskDrawdownPercent = riskThreshold
        });
        var analysis = report.Analyses[0];
        Assert.Equal(highRisk, analysis.IsHighRisk);
        Assert.Equal(highPerforming, analysis.IsHighPerforming);
        Assert.Equal(promising, analysis.IsPromising);
        Assert.Equal(riskThreshold, report.HighRiskDrawdownPercent);
    }

    [Fact]
    public void Analyze_MissingSnapshotOrBenchmark_NeverInventsParameterEvidence()
    {
        var missingSnapshot = Run(1) with { Configuration = null };
        var missingBenchmark = Run(2);
        missingBenchmark.Summary.BenchmarkComparisons = [];
        var report = _service.Analyze(new StrategyTuningRequest { Runs = [missingSnapshot, missingBenchmark] });
        Assert.All(report.Analyses, analysis => Assert.False(analysis.IsPromising));
        Assert.Null(report.Analyses[0].ConfigurationJson);
        Assert.Contains(report.Analyses[0].Limitations, text => text.Contains("snapshot is missing"));
        Assert.False(report.Analyses[1].IsHighPerforming);
        Assert.Contains(report.Analyses[1].Limitations, text => text.Contains("unavailable"));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(10, null, false)]
    [InlineData(10, 0, true)]
    public void Analyze_NoTradesOrUnrecoveredDrawdown_PreventsPromising(
        int trades, int? recoveryDays, bool promising)
    {
        var run = Run(1);
        run.Summary.TradesCount = trades;
        run.Summary.RecoveryTimeDays = recoveryDays;
        var analysis = _service.Analyze(new StrategyTuningRequest { Runs = [run, Run(2)] }).Analyses[0];
        Assert.Equal(promising, analysis.IsPromising);
        if (!promising)
            Assert.NotEmpty(analysis.Weaknesses);
    }

    [Fact]
    public void Analyze_AcceptsCapitalAndReturnWithIndependentFourDecimalRounding()
    {
        const decimal initialCapital = 100.123456m;
        const decimal finalCapital = 112.3456m;
        var reportedReturn = Math.Round((finalCapital - initialCapital) / initialCapital * 100m, 4);
        var roundedInitialCapital = Math.Round(initialCapital, 4);
        var roundedRun = Run(1, returns: reportedReturn);
        roundedRun.Summary.StartCapital = roundedInitialCapital;
        roundedRun.Summary.FinalCapital = finalCapital;

        var otherRun = Run(2, "other", 1m);
        otherRun.Summary.StartCapital = roundedInitialCapital;
        otherRun.Summary.FinalCapital = Math.Round(roundedInitialCapital * 1.01m, 4);

        var report = _service.Analyze(new StrategyTuningRequest { Runs = [roundedRun, otherRun] });

        Assert.Equal(reportedReturn, report.Analyses.Single(analysis => analysis.BacktestRunId == 1).Metrics.ReturnPercent);
    }

    [Fact]
    public void Analyze_UnderperformanceAndBenchmarkMatch_AreWeaknesses()
    {
        var report = _service.Analyze(new StrategyTuningRequest { Runs = [Run(1, returns: 9m), Run(2, returns: 10m)] });
        Assert.All(report.Analyses, analysis =>
        {
            Assert.False(analysis.IsHighPerforming);
            Assert.False(analysis.IsPromising);
            Assert.Contains(analysis.Weaknesses, text => text.Contains("Did not outperform"));
        });
    }

    [Theory]
    [InlineData(BacktestRunStatus.Failed, false)]
    [InlineData(BacktestRunStatus.Running, true)]
    [InlineData(BacktestRunStatus.Completed, false)]
    public void Analyze_ExcludesUnsuccessfulOrIncompleteRuns(BacktestRunStatus status, bool success)
    {
        var excluded = Run(2);
        excluded.Summary.Status = status;
        excluded.Summary.IsSuccess = success;
        excluded.Summary.ErrorMessage = "No usable results";
        var report = _service.Analyze(new StrategyTuningRequest { Runs = [Run(1), excluded] });
        Assert.False(report.Analyses[1].IsAnalyzable);
        Assert.False(report.Analyses[1].IsHighRisk);
        Assert.Empty(report.Analyses[1].Strengths);
        Assert.Contains(report.Analyses[1].Limitations, text => text.Contains("No usable results"));
        Assert.Null(report.Analyses.Single(entry => entry.BacktestRunId == 2).Metrics.Rank);
        Assert.False(report.Analyses[0].IsPromising);
        Assert.Contains(report.Analyses[0].Limitations, text => text.Contains("Fewer than two"));
    }

    [Fact]
    public void Analyze_AllFailedRuns_HaveNoPeriodOrWinner()
    {
        var runs = new[] { Run(1), Run(2) };
        foreach (var run in runs)
            run.Summary.IsSuccess = false;
        var report = _service.Analyze(new StrategyTuningRequest { Runs = runs });
        Assert.Null(report.StartDate);
        Assert.Null(report.EndDate);
        Assert.All(report.Analyses, analysis => Assert.Null(analysis.Metrics.Rank));
        Assert.All(report.Analyses, analysis => Assert.False(analysis.IsAnalyzable));
    }

    [Fact]
    public void Analyze_IsDeterministic_WithRepeatedStrategyIdsAndMetricTies()
    {
        var runs = new[] { Run(3, "B"), Run(2, "A"), Run(1, "A") };
        var first = _service.Analyze(new StrategyTuningRequest { Runs = runs });
        var reversed = _service.Analyze(new StrategyTuningRequest { Runs = runs.Reverse().ToArray() });
        Assert.Equal(new[] { 1, 2, 3 }, first.Analyses.OrderBy(analysis => analysis.Metrics.Rank).Select(entry => entry.BacktestRunId));
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(reversed));
    }

    [Theory]
    [InlineData("dates")]
    [InlineData("capital")]
    [InlineData("configuration")]
    [InlineData("benchmark")]
    [InlineData("duplicate-benchmark")]
    [InlineData("drawdown")]
    [InlineData("negative-trades")]
    [InlineData("missing-date")]
    [InlineData("inconsistent-relative-return")]
    [InlineData("capital-return")]
    [InlineData("null-benchmark")]
    public void Analyze_RejectsInvalidOrIncomparableCompletedRuns(string invalid)
    {
        var run = Run(1);
        switch (invalid)
        {
            case "dates": run.Summary.EndDate = run.Summary.EndDate.AddDays(1); break;
            case "capital": run.Summary.StartCapital = 50m; break;
            case "configuration": run.Configuration!.Id = "mismatch"; break;
            case "benchmark":
                run.Summary.BenchmarkComparisons = [new() { Symbol = "SPY", BenchmarkReturnPercent = 99m }];
                break;
            case "duplicate-benchmark":
                run.Summary.BenchmarkComparisons = [run.Summary.BenchmarkComparisons[0], run.Summary.BenchmarkComparisons[0]];
                break;
            case "drawdown": run.Summary.MaxDrawdownPercent = -1m; break;
            case "negative-trades": run.Summary.TradesCount = -1; break;
            case "missing-date": run.Summary.StartDate = default; break;
            case "inconsistent-relative-return":
                run.Summary.BenchmarkComparisons = [new() { Symbol = "SPY", BenchmarkReturnPercent = 10m,
                    StrategyReturnPercent = 20m, RelativeReturnPercent = 50m, AlphaPercent = 50m }];
                break;
            case "capital-return": run.Summary.FinalCapital = 50m; break;
            case "null-benchmark": run.Summary.BenchmarkComparisons = [null!]; break;
        }
        Assert.Throws<ArgumentException>(() => _service.Analyze(new StrategyTuningRequest { Runs = [run, Run(2)] }));
    }

    [Fact]
    public void Analyze_RejectsInvalidRequestAndDuplicateIdentity()
    {
        var valid = new StrategyTuningRequest { Runs = [Run(1), Run(2)] };
        StrategyTuningRequest[] invalid =
        [
            valid with { SchemaVersion = 2 }, valid with { Runs = [] },
            valid with { Runs = [Run(1)] }, valid with { Runs = [Run(1), Run(1)] },
            valid with { Runs = [Run(0), Run(2)] }, valid with { BenchmarkSymbol = "" },
            valid with { MinimumReturnPercent = -1m }, valid with { HighRiskDrawdownPercent = 0m },
            valid with { HighRiskDrawdownPercent = 101m }, valid with { Runs = null! },
            valid with { Runs = [null!, Run(2)] }
        ];
        Assert.Throws<ArgumentNullException>(() => _service.Analyze(null!));
        foreach (var request in invalid)
            Assert.Throws<ArgumentException>(() => _service.Analyze(request));
    }

    internal static StrategyTuningRun Run(int id, string strategy = "variant", decimal returns = 20m, decimal drawdown = 5m) =>
        new()
        {
            Configuration = new StrategyConfiguration { Id = strategy },
            Summary = new BacktestSummaryReportResult
            {
                BacktestRunId = id, StrategyId = strategy, IsSuccess = true, Status = BacktestRunStatus.Completed,
                StartDate = new(2025, 1, 1), EndDate = new(2025, 12, 31),
                StartCapital = 100m, FinalCapital = 100m + returns, ReturnPercent = returns,
                MaxDrawdownPercent = drawdown, RecoveryTimeDays = 3, TradesCount = 10,
                BenchmarkComparisons =
                [
                    new() { Symbol = "SPY", BenchmarkReturnPercent = 10m, StrategyReturnPercent = returns,
                        RelativeReturnPercent = returns - 10m, AlphaPercent = returns - 10m }
                ]
            }
        };
}
