using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class CompletedMarketSessionTests
{
    private static MarketCandle Candle(DateTime date, decimal price = 100m) =>
        new() { Date = date, Open = price, High = price, Low = price, Close = price, Volume = 100m };

    private static MarketCandle MalformedCandle(DateTime date)
    {
        var candle = Candle(date, 103m);
        candle.High = 90m;
        candle.Low = 110m;
        return candle;
    }

    [Theory]
    [InlineData("2024-07-06T03:59:59Z", "2024-07-04")]
    [InlineData("2024-07-06T04:00:00Z", "2024-07-05")]
    [InlineData("2024-01-06T04:59:59Z", "2024-01-04")]
    [InlineData("2024-01-06T05:00:00Z", "2024-01-05")]
    public void CompletionUsesNextNewYorkMidnight_IncludingDst(string timestamp, string expected)
    {
        var now = DateTimeOffset.Parse(timestamp);
        var todayUtc = now.UtcDateTime.Date;
        var candles = Enumerable.Range(0, 4).Select(x => Candle(todayUtc.AddDays(-x))).ToList();
        var session = CompletedMarketSession.Create(["AAA"],
            new Dictionary<string, IReadOnlyList<MarketCandle>> { ["AAA"] = candles }, now);
        Assert.Equal(DateOnly.Parse(expected), session.SessionDate);
    }

    [Fact]
    public void NoWeekdayCalendarIsInferred_AndReturnedPriceSetIsImmutable()
    {
        var date = new DateTime(2024, 1, 6); // Synthetic Saturday candle: trust supplied session identity, not a calendar.
        var candle = Candle(date);
        var input = new Dictionary<string, IReadOnlyList<MarketCandle>> { ["AAA"] = [candle] };
        var session = CompletedMarketSession.Create(["AAA"], input, PaperWorkflowTestSupport.Clock.GetUtcNow());
        candle.Close = 999m;
        Assert.Equal(new DateOnly(2024, 1, 6), session.SessionDate);
        Assert.Equal(100m, session.Prices["AAA"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, decimal>)session.Prices)["AAA"] = 1m);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void VendorDailyDate_IsPreservedAsALabel_NotConvertedAsAnInstant(DateTimeKind kind)
    {
        var label = new DateTime(2024, 1, 7, 0, 0, 0, kind);
        var session = CompletedMarketSession.Create(["AAA"],
            new Dictionary<string, IReadOnlyList<MarketCandle>> { ["AAA"] = [Candle(label)] },
            PaperWorkflowTestSupport.Clock.GetUtcNow());
        Assert.Equal(new DateOnly(2024, 1, 7), session.SessionDate);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mixed")]
    [InlineData("duplicate")]
    [InlineData("incomplete")]
    [InlineData("stale")]
    [InlineData("invalid")]
    [InlineData("malformedhistory")]
    public void AmbiguousIncompleteOrStalePrices_AreRejected(string scenario)
    {
        var date = new DateTime(2024, 1, 7);
        var data = new Dictionary<string, IReadOnlyList<MarketCandle>>
        {
            ["AAA"] = [Candle(date)],
            ["BBB"] = [Candle(date)]
        };
        switch (scenario)
        {
            case "missing": data.Remove("BBB"); break;
            case "mixed": data["BBB"] = [Candle(date.AddDays(-1))]; break;
            case "duplicate": data["AAA"] = [Candle(date), Candle(date, 101m)]; break;
            case "incomplete": data["AAA"] = [Candle(date.AddDays(1))]; break;
            case "stale": data["AAA"] = data["BBB"] = [Candle(date.AddDays(-7))]; break;
            case "invalid": data["AAA"] = [Candle(date, 0m)]; break;
            case "malformedhistory":
                data["AAA"] = [MalformedCandle(date.AddDays(-1)), Candle(date)];
                break;
        }
        Assert.Throws<InvalidOperationException>(() =>
            CompletedMarketSession.Create(["AAA", "BBB"], data, PaperWorkflowTestSupport.Clock.GetUtcNow()));
    }

    [Fact]
    public async Task PaperCollection_AnalyzesOnlyCompletedCandles_AndCarriesTheirExactSessionAndPrices()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var context = new TradingSimulatorDbContext(options);
        var strategy = new StrategyConfiguration { UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(new Provider(),
            new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(context), new FeatureSnapshotRepository(context), strategy,
            timeProvider: PaperWorkflowTestSupport.Clock);
        var result = await workflow.CollectAndAnalyzeCompletedSessionAsync(["AAA", "BBB"], new DateTime(2024, 1, 1));
        Assert.NotNull(result.ExecutionSession);
        Assert.Equal(new DateOnly(2024, 1, 7), result.ExecutionSession.SessionDate);
        Assert.All(result.Prices.Values, x => Assert.Equal(106m, x));
        Assert.All(result.ExecutionSession.Prices.Values, x => Assert.Equal(106m, x));
        Assert.Equal(2, result.RankedResults.Count);
        Assert.Equal(2, await context.FeatureSnapshots.CountAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mixed")]
    [InlineData("incomplete")]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("short")]
    [InlineData("analysisfailure")]
    [InlineData("malformedhistory")]
    public async Task InconsistentCollection_PermitsAnalysisButNeverAuthorizesExecution(string scenario)
    {
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var strategy = new StrategyConfiguration { UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(new Provider(scenario),
            new MarketObservationAnalyzer(scenario == "analysisfailure"
                ? new ThrowForBBBEngine() : ScoringEngineFactory.Create(strategy)), new PortfolioRecommendationService(),
            new ObservationResultRepository(context), new FeatureSnapshotRepository(context), strategy,
            timeProvider: PaperWorkflowTestSupport.Clock);
        var result = await workflow.CollectAndAnalyzeCompletedSessionAsync(["AAA", "BBB"], new DateTime(2024, 1, 1));
        Assert.Null(result.ExecutionSession);
        Assert.NotEmpty(result.ExecutionRejection!);
        Assert.Empty(await context.Portfolios.ToListAsync());
        Assert.Empty(await context.Trades.ToListAsync());
        Assert.Empty(await context.PortfolioSnapshots.ToListAsync());
        Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
        Assert.NotEmpty(await context.ObservationRuns.ToListAsync());
    }

    [Fact]
    public async Task AnalyticalCollection_DoesNotAuthorizeExecution()
    {
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var strategy = new StrategyConfiguration { UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(new Provider(),
            new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(context), new FeatureSnapshotRepository(context), strategy,
            timeProvider: PaperWorkflowTestSupport.Clock);
        var result = await workflow.CollectAndAnalyzeAsync(["AAA"], new DateTime(2024, 1, 1));
        Assert.Null(result.ExecutionSession);
        Assert.NotNull(result.ExecutionRejection);
    }

    [Fact]
    public async Task BenchmarkInsideUniverse_ReusesTheExactFetchUsedForRelativeStrength()
    {
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var provider = new RevisingBenchmarkProvider();
        var strategy = new StrategyConfiguration { UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "AAA" };
        var workflow = new MarketDataWorkflow(provider,
            new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(context), new FeatureSnapshotRepository(context), strategy,
            timeProvider: PaperWorkflowTestSupport.Clock);
        var result = await workflow.CollectAndAnalyzeCompletedSessionAsync(["AAA", "BBB"], new DateTime(2024, 1, 1));
        Assert.NotNull(result.ExecutionSession);
        Assert.Equal(1, provider.BenchmarkCalls);
        Assert.Equal(new DateOnly(2024, 1, 7), result.ExecutionSession.SessionDate);
        Assert.Equal(106m, result.ExecutionSession.Prices["AAA"]);
        Assert.Equal(result.Prices["AAA"], result.ExecutionSession.Prices["AAA"]);
        Assert.Equal(2, result.RankedResults.Count);
    }

    [Fact]
    public async Task MissingFundamentals_DoNotCountAsFailedOrSkippedPriceAnalysis()
    {
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var strategy = new StrategyConfiguration { UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(new Provider(),
            new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(context), new FeatureSnapshotRepository(context), strategy,
            fundamentalDataProvider: new MissingFundamentals(), timeProvider: PaperWorkflowTestSupport.Clock);
        var result = await workflow.CollectAndAnalyzeCompletedSessionAsync(["AAA", "BBB"], new DateTime(2024, 1, 1));
        Assert.NotNull(result.ExecutionSession);
        Assert.Equal(2, result.RankedResults.Count);
        Assert.All(result.RankedResults, x => Assert.Null(x.Features!.Fundamentals));
    }

    private sealed class ThrowForBBBEngine : IScoringEngine
    {
        public decimal CalculateScore(ScoringContext context) => context.Symbol == "BBB"
            ? throw new InvalidOperationException("Injected analyzer failure.") : 10m;
    }

    private sealed class MissingFundamentals : IFundamentalDataProvider
    {
        public Task<FundamentalData?> GetFundamentalDataAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult<FundamentalData?>(null);
    }

    private sealed class RevisingBenchmarkProvider : IMarketDataProvider
    {
        public int BenchmarkCalls { get; private set; }
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from)
        {
            if (symbol == "AAA" && ++BenchmarkCalls > 1)
                return Task.FromResult<IReadOnlyList<MarketCandle>>([Candle(new DateTime(2024, 1, 6), 999m)]);
            return new Provider().GetHistoricalDailyAsync(symbol, from);
        }
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to) =>
            GetHistoricalDailyAsync(symbol, from);
    }

    private sealed class Provider(string? scenario = null) : IMarketDataProvider
    {
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from)
        {
            var candles = Enumerable.Range(0, 8)
                .Select(x => Candle(new DateTime(2024, 1, 1).AddDays(x), 100m + x)).ToList();
            if (symbol == "BBB")
            {
                switch (scenario)
                {
                    case "missing": candles = []; break;
                    case "mixed": candles.RemoveAt(6); break;
                    case "incomplete": candles = [candles[^1]]; break;
                    case "invalid": candles[6] = Candle(new DateTime(2024, 1, 7), 0m); break;
                    case "duplicate": candles.Add(Candle(new DateTime(2024, 1, 7))); break;
                    case "short": candles = candles.Skip(3).ToList(); break;
                    case "malformedhistory":
                        candles[5] = MalformedCandle(new DateTime(2024, 1, 6));
                        break;
                }

            }
            return Task.FromResult<IReadOnlyList<MarketCandle>>(candles);
        }
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to) =>
            GetHistoricalDailyAsync(symbol, from);
    }
}
