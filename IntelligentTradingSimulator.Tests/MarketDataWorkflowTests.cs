using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

[Collection("Console output")]
public sealed class MarketDataWorkflowTests : IDisposable
{
    private readonly DbContextOptions<TradingSimulatorDbContext> _options;
    private readonly TradingSimulatorDbContext _dbContext;

    public MarketDataWorkflowTests()
    {
        _options =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

        _dbContext = new TradingSimulatorDbContext(_options);
    }

    public void Dispose() => _dbContext.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectAndAnalyzeAsync_PreservesWarningsAndForcesFinalProgressSummary(bool progressEnabled)
    {
        var date = new DateTime(2024, 1, 10);
        using var progressOutput = new StringWriter();
        using var progress = progressEnabled
            ? new ConsoleWorkflowProgress(progressOutput, interval: TimeSpan.FromDays(1))
            : null;
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<MarketCandle>>
        {
            ["SKIP"] = BuildCandles(date, 1)
        });
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "SPY"
        };
        var workflow = new MarketDataWorkflow(
            provider, new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext), new FeatureSnapshotRepository(_dbContext),
            strategy, timeProvider: new FixedTimeProvider(date), progress: progress);
        using var output = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            await workflow.CollectAndAnalyzeAsync(["SKIP", "FAIL"], date);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("Benchmark data unavailable for SPY.", output.ToString());
        Assert.Contains("Pomijam SKIP - za mało świeczek: 1", output.ToString());
        Assert.Contains("Błąd pobierania danych dla FAIL:", output.ToString());
        Assert.DoesNotContain("Symbols processed:", output.ToString());
        if (progressEnabled)
        {
            Assert.Contains("Symbols processed: 0 analyzed, 1 skipped, 1 failed", progressOutput.ToString());
        }
        else
        {
            Assert.Equal("", progressOutput.ToString());
        }
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_ReportsEmptyBenchmarkWithoutRetryingProvider()
    {
        var date = new DateTime(2024, 1, 10);
        using var progressOutput = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(
            progressOutput, interval: TimeSpan.FromDays(1));
        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["SPY"] = [],
                ["PASS"] = BuildCandles(date.AddDays(-4), 5)
            });
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = true,
            RelativeStrengthBenchmarkSymbol = "SPY"
        };
        var workflow = new MarketDataWorkflow(
            provider, new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext), new FeatureSnapshotRepository(_dbContext),
            strategy, timeProvider: new FixedTimeProvider(date), progress: progress);

        await workflow.CollectAndAnalyzeAsync(["PASS"], date.AddDays(-4));

        Assert.Equal(new[] { "SPY", "PASS" }, provider.Calls);
        Assert.Single(progressOutput.ToString().Split(Environment.NewLine)
            .Where(line => line.StartsWith("Benchmark data unavailable for SPY")));
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_ConsoleProgressForcesOneAggregateFundamentalsWarning()
    {
        var date = new DateTime(2024, 1, 10);
        using var progressOutput = new StringWriter();
        using var progress = new ConsoleWorkflowProgress(
            progressOutput, interval: TimeSpan.FromDays(1));
        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["AAA"] = BuildCandles(date.AddDays(-4), 5),
                ["BBB"] = BuildCandles(date.AddDays(-4), 5),
                ["CCC"] = BuildCandles(date.AddDays(-4), 5)
            });
        var fundamentalCalls = 0;
        var fundamentals = new CallbackFundamentalDataProvider(_ =>
        {
            fundamentalCalls++;
            return Task.FromResult<FundamentalData?>(null);
        });
        var strategy = new StrategyConfiguration { UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(
            provider, new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext), new FeatureSnapshotRepository(_dbContext),
            strategy, fundamentals, new FixedTimeProvider(date), progress);

        await workflow.CollectAndAnalyzeAsync(["AAA", "BBB", "CCC"], date.AddDays(-4));

        var warning = Assert.Single(progressOutput.ToString().Split(Environment.NewLine)
            .Where(line => line.StartsWith("Fundamentals unavailable for 3 symbols")));
        Assert.Contains("Continuing without fundamentals", warning);
        Assert.Equal(3, fundamentalCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CollectAndAnalyzeAsync_ProgressCountsAllSymbolOutcomesWithoutExtraFetches(int fundamentalsMode)
    {
        var date = new DateTime(2024, 1, 10);
        var progress = new SimulationProgressRecorder
        {
            Context = new WorkflowProgressContext
            {
                Workflow = "stale", RunId = 999, CompletedDates = 99, TotalDates = 99,
                VariantIndex = 9, TotalVariants = 9
            }
        };
        var provider = new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<MarketCandle>>
        {
            ["PASS"] = BuildCandles(date.AddDays(-4), 5),
            ["SKIP"] = BuildCandles(date, 1)
        })
        {
            OnFetch = _ => Assert.Equal(1, progress.ActiveOperations)
        };
        var fundamentalCalls = 0;
        IFundamentalDataProvider? fundamentals = fundamentalsMode == 0 ? null :
            new CallbackFundamentalDataProvider(_ =>
            {
                fundamentalCalls++;
                Assert.Equal(1, progress.ActiveOperations);
                if (fundamentalsMode == 2)
                    throw new HttpRequestException("Private upstream detail");
                return Task.FromResult<FundamentalData?>(fundamentalsMode == 3
                    ? new FundamentalData { Symbol = "PASS", AsOfDate = DateOnly.FromDateTime(date.AddDays(1)) }
                    : null);
            });
        var strategy = new StrategyConfiguration { Id = "daily", UseRelativeStrength = false };
        var workflow = new MarketDataWorkflow(
            provider, new MarketObservationAnalyzer(strategy), new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext), new FeatureSnapshotRepository(_dbContext),
            strategy, fundamentals, new FixedTimeProvider(date), progress);
        var enumerations = 0;
        IEnumerable<string> Symbols()
        {
            enumerations++;
            Assert.Equal(1, enumerations);
            yield return "PASS";
            yield return "SKIP";
            yield return "FAIL";
        }

        var result = await workflow.CollectAndAnalyzeAsync(Symbols(), date.AddDays(-4));

        Assert.Single(result.RankedResults);
        Assert.Equal(new[] { "PASS", "SKIP", "FAIL" }, provider.Calls);
        Assert.Equal(fundamentalsMode == 0 ? 0 : 1, fundamentalCalls);
        Assert.Equal(1, enumerations);
        Assert.Equal("market-data", progress.Context.Workflow);
        Assert.Equal("daily", progress.Context.StrategyId);
        Assert.Equal(date, progress.Context.Date);
        Assert.Null(progress.Context.RunId);
        Assert.Null(progress.Context.CompletedDates);
        Assert.Null(progress.Context.TotalDates);
        Assert.Null(progress.Context.VariantIndex);
        Assert.Null(progress.Context.TotalVariants);
        Assert.Equal(3, progress.Context.TotalSymbols);
        Assert.Equal(3, progress.Context.CompletedSymbols);
        var symbolReports = progress.Reports.Where(x => x.Message.StartsWith("Symbols processed:")).ToList();
        Assert.Equal(new int?[] { 1, 2, 3, 3 }, symbolReports.Select(x => x.Context.CompletedSymbols));
        Assert.Equal("Symbols processed: 1 analyzed, 1 skipped, 1 failed", symbolReports.Last().Message);
        Assert.True(symbolReports.Last().Force);
        Assert.Equal(3, progress.Operations.Count(x => x.Operation == "Fetching price data"));
        Assert.Equal(fundamentalsMode == 0 ? 0 : 1,
            progress.Operations.Count(x => x.Operation == "Fetching fundamental data"));
        Assert.Contains(progress.Reports, x => x.Message.Contains(
            fundamentalsMode == 0 ? "no provider configured" :
            fundamentalsMode == 2 ? "Continuing without fundamentals" : "Fundamentals unavailable"));
        Assert.DoesNotContain(progress.Reports, x => x.Message.Contains("Private upstream detail"));
        Assert.Contains(progress.Operations, x => x.Operation == "Building rankings");
        Assert.Contains(progress.Operations, x => x.Operation == "Saving observations");
        Assert.Contains(progress.Operations, x => x.Operation == "Saving feature snapshots");
        Assert.Contains(progress.Operations, x => x.Operation == "Loading previous run");
        Assert.Equal(0, progress.ActiveOperations);
    }

    [Theory]
    [InlineData(ScoringEngineType.Composite)]
    [InlineData(ScoringEngineType.RuleBased)]
    [InlineData(ScoringEngineType.AI)]
    public async Task CollectAndAnalyzeAsync_PersistsDatedFeatures_WithoutFutureInputs(
        ScoringEngineType scoringEngine)
    {
        var date = new DateTime(2024, 1, 10);
        var candles = BuildCandles(date.AddDays(-4), 5);
        candles.Add(new MarketCandle
        {
            Date = date.AddDays(1), Close = 1000m, High = 2000m, Low = 1m, Volume = 100m
        });
        var strategy = new StrategyConfiguration
        {
            Id = "daily", UseRelativeStrength = false, ScoringEngine = scoringEngine
        };
        var fundamentals = new FakeFundamentalDataProvider(new Dictionary<string, FundamentalData?>
        {
            ["TEST"] = new FundamentalData
            {
                Symbol = "TEST", AsOfDate = new DateOnly(2024, 1, 11), RevenueGrowthPercent = 999m
            }
        });
        var workflow = new MarketDataWorkflow(
            new FakeMarketDataProvider(candles),
            new MarketObservationAnalyzer(ScoringEngineFactory.Create(strategy)),
            new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext),
            new FeatureSnapshotRepository(_dbContext),
            strategy, fundamentals, new FixedTimeProvider(date));

        var result = await workflow.CollectAndAnalyzeAsync(["TEST"], date.AddDays(-4));
        await workflow.CollectAndAnalyzeAsync(["TEST"], date.AddDays(-4));

        await using var readContext = new TradingSimulatorDbContext(_options);
        var snapshot = Assert.Single(await new FeatureSnapshotRepository(readContext)
            .QueryAsync(new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10), "TEST", "daily"));
        var observation = Assert.Single(result.RankedResults);
        Assert.Equal(observation.Score, snapshot.Score);
        Assert.Equal(4m, snapshot.Features.MomentumPercent);
        Assert.Null(snapshot.Features.Fundamentals);
        Assert.Null(snapshot.BacktestRunId);
        Assert.Equal(observation.Features!.AnalysisDate, snapshot.AnalysisDate);
    }

    private sealed class FixedTimeProvider(DateTime date) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(date, DateTimeKind.Utc));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectAndAnalyzeAsync_TrailingStopRetainsPriceWithoutScoringHistory(bool enabled)
    {
        var date = new DateTime(2024, 1, 10);
        var provider = new FakeMarketDataProvider(
        [
            new MarketCandle { Date = date, Close = 108m },
            new MarketCandle { Date = DateTime.MaxValue, Close = 1000m }
        ]);
        var workflow = BuildWorkflow(provider, new StrategyConfiguration
        {
            UseRelativeStrength = false, TrailingStopPercent = enabled ? 10m : null
        });

        var result = await workflow.CollectAndAnalyzeAsync(["TEST"], date.AddDays(-10));

        Assert.Empty(result.RankedResults);
        Assert.Empty(result.Recommendations);
        Assert.Equal(108m, result.Prices["TEST"]);
        Assert.Empty(_dbContext.FeatureSnapshots);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectAndAnalyzeAsync_StopLossRetainsAvailablePriceWithoutScoringHistory(bool enabled)
    {
        var today = DateTime.Today;
        var provider = new FakeMarketDataProvider(
        [
            new MarketCandle { Date = today, Close = 90m },
            new MarketCandle { Date = today.AddDays(1), Close = 1m }
        ]);
        var workflow = BuildWorkflow(provider, new StrategyConfiguration
        {
            UseRelativeStrength = false, StopLossPercent = enabled ? 10m : null
        });

        var result = await workflow.CollectAndAnalyzeAsync(["TEST"], today.AddDays(-10));

        Assert.Empty(result.RankedResults);
        Assert.Empty(result.Recommendations);
        Assert.Equal(90m, result.Prices["TEST"]);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_TracksHighWhileDisabled_ThenExitsAfterEnablingTrailingStop()
    {
        var date = new DateTime(2024, 1, 10);
        var portfolio = new Portfolio { Name = "Current", StartingCash = 1000m, Cash = 1000m };
        new TradingEngine().Execute(portfolio,
            new TradingDecision { Symbol = "TEST", Action = TradeActionType.Buy, Amount = 1000m }, 100m);
        await new PortfolioRepository(_dbContext).SaveAsync(portfolio);

        async Task Run(decimal price, decimal? trailingStopPercent)
        {
            await using var context = new TradingSimulatorDbContext(_options);
            var strategy = new StrategyConfiguration
            {
                UseRelativeStrength = false, TrailingStopPercent = trailingStopPercent
            };
            var marketWorkflow = new MarketDataWorkflow(
                new FakeMarketDataProvider([new MarketCandle { Date = date, Close = price }]),
                new MarketObservationAnalyzer(strategy),
                new PortfolioRecommendationService(),
                new ObservationResultRepository(context),
                new FeatureSnapshotRepository(context),
                strategy);
            var result = await marketWorkflow.CollectAndAnalyzeAsync(["TEST"], date.AddDays(-10));
            Assert.Empty(result.RankedResults);
            Assert.Empty(result.Recommendations);
            var workflow = new PortfolioWorkflow(
                new PortfolioRepository(context),
                new PortfolioSnapshotRepository(context),
                new PositionSnapshotRepository(context),
                new PortfolioExecutionService(),
                new TradingEngine(),
                new PortfolioAllocationEngine(new PortfolioAllocationService()),
                new PortfolioRebalancingService(),
                new PortfolioPerformanceService(),
                new ConsoleReporter(new PortfolioAllocationService(), new PortfolioContributionService()),
                strategy);
            await workflow.RunAsync(result.Recommendations, result.Prices);
        }

        await Run(150m, null);
        await using (var context = new TradingSimulatorDbContext(_options))
        {
            var saved = await new PortfolioRepository(context).GetLatestAsync();
            var position = Assert.Single(saved!.Positions);
            Assert.Equal(150m, position.CurrentPrice);
            Assert.Equal(150m, position.HighestPriceSinceEntry);
            Assert.DoesNotContain(saved.Trades, trade => trade.Action == TradeActionType.Sell);
        }

        await Run(135m, 10m);
        await using var readContext = new TradingSimulatorDbContext(_options);
        var exited = await new PortfolioRepository(readContext).GetLatestAsync();
        Assert.Empty(exited!.Positions);
        var sell = Assert.Single(exited.Trades, trade => trade.Action == TradeActionType.Sell);
        Assert.Equal(ExitReason.TrailingStop, sell.ExitReason);
        Assert.Equal(135m, sell.Price);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_ReturnsNullWeek52Values_WhenProviderHasFewerThan252Sessions()
    {
        var today = DateTime.Today;
        var provider = new FakeMarketDataProvider(
            BuildCandles(today.AddDays(-250), 251));
        var workflow = BuildWorkflow(provider);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["TEST"],
                today.AddDays(-89));

        var observation = Assert.Single(result.RankedResults);
        Assert.Null(observation.Week52High);
        Assert.Null(observation.DistanceFromWeek52HighPercent);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_ExposesWeek52Values_WithoutChangingLookbackScoring()
    {
        var today = DateTime.Today;
        var candles = BuildCandles(today.AddDays(-251), 252);
        candles[0].High = 1_000m;
        var provider = new FakeMarketDataProvider(candles);
        var strategy = new StrategyConfiguration
        {
            LookbackDays = 89,
            UseRsi = true
        };
        var workflow = BuildWorkflow(provider, strategy);
        var requestedStartDate = today.AddDays(-89);
        var strategyCandles =
            candles
                .Where(x => x.Date.Date >= requestedStartDate)
                .ToList();
        var expected =
            new MarketObservationAnalyzer(strategy)
                .Analyze("TEST", strategyCandles);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["TEST"],
                requestedStartDate);

        var observation = Assert.Single(result.RankedResults);
        Assert.Equal(1_000m, observation.Week52High);
        Assert.Equal(
            (observation.Week52High.HasValue
                ? (candles[^1].Close - observation.Week52High.Value)
                    / observation.Week52High.Value * 100m
                : null),
            observation.DistanceFromWeek52HighPercent);
        Assert.Equal(expected.Rsi, observation.Rsi);
        Assert.Equal(expected.MomentumPercent, observation.MomentumPercent);
        Assert.Equal(expected.Score, observation.Score);
        Assert.Equal(
            MarketHistoryRequirements.GetEffectiveStartDate(
                requestedStartDate,
                today),
            provider.LastFrom);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_RanksByWeek52HighAdjustedScore_WhenEnabled()
    {
        var today = DateTime.Today;
        var nearHighCandles = BuildCandles(today.AddDays(-251), 252);
        var farBelowHighCandles = BuildCandles(today.AddDays(-251), 252);
        farBelowHighCandles[0].High = 1_000m;

        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["FAR"] = farBelowHighCandles,
                ["NEAR"] = nearHighCandles
            });
        var strategy = new StrategyConfiguration
        {
            LookbackDays = 89,
            Use52WeekHigh = true,
            Week52HighScoreWeight = 10m
        };
        var workflow = BuildWorkflow(provider, strategy);

        var result =
            await workflow.CollectAndAnalyzeAsync(
                ["FAR", "NEAR"],
                today.AddDays(-89));

        Assert.Collection(
            result.RankedResults,
            first => Assert.Equal("NEAR", first.Symbol),
            second => Assert.Equal("FAR", second.Symbol));
        Assert.True(
            result.RankedResults[0].Score >
            result.RankedResults[1].Score);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectAndAnalyzeAsync_UsesSelectedBenchmarkOnce_AndAdjustsScore(bool enabled)
    {
        var start = new DateTime(2024, 1, 1);
        var stock = BuildCandles(start, 5);
        var benchmark = BuildCandles(start, 5);
        benchmark[^1].Close = 110m;
        stock.Add(new MarketCandle { Date = DateTime.MaxValue, Close = 10_000m });
        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["A"] = stock, ["B"] = stock, ["QQQ"] = benchmark
            });
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = enabled,
            RelativeStrengthBenchmarkSymbol = "QQQ"
        };
        var workflow = BuildWorkflow(provider, strategy);

        var result = await workflow.CollectAndAnalyzeAsync(["A", "B", "MISSING"], start);

        Assert.Equal(2, result.RankedResults.Count);
        Assert.Equal(enabled ? 1 : 0, provider.Calls.Count(x => x == "QQQ"));
        Assert.All(result.RankedResults, observation =>
        {
            Assert.Equal(enabled ? -6m : (decimal?)null, observation.RelativeStrengthPercent);
            Assert.Equal(enabled ? "QQQ" : null, observation.RelativeStrengthBenchmarkSymbol);
            Assert.Equal(4m, observation.MomentumPercent);
            var expected = new MarketObservationAnalyzer(strategy)
                .Analyze(observation.Symbol, stock.Take(5).ToList());
            Assert.Equal(expected.Score + (enabled ? -6m : 0m), observation.Score);
            Assert.Equal(enabled ? -6m : 0m, observation.RelativeStrengthScoreContribution);
        });
    }

    [Theory]
    [InlineData(false, 20, "WEAK")]
    [InlineData(true, 0, "WEAK")]
    [InlineData(true, 20, "STRONG")]
    public async Task CollectAndAnalyzeAsync_RanksByRelativeStrengthAdjustedScore(
        bool enabled, int weight, string expectedFirst)
    {
        var start = new DateTime(2024, 1, 1);
        var strong = BuildCandles(start, 5);
        var weak = BuildCandles(start, 5);
        weak[^1].Close = 103m;
        weak[^1].Volume = 5_000_000m;
        var benchmark = BuildCandles(start, 5);
        benchmark[^1].Close = 103.5m;
        var provider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["WEAK"] = weak, ["STRONG"] = strong, ["SPY"] = benchmark
            });
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = enabled,
            RelativeStrengthScoreWeight = weight
        };

        var result = await BuildWorkflow(provider, strategy)
            .CollectAndAnalyzeAsync(["WEAK", "STRONG"], start);

        Assert.Equal(expectedFirst, result.RankedResults[0].Symbol);
        Assert.True(result.RankedResults[0].Score > result.RankedResults[1].Score);
        if (enabled)
        {
            Assert.Equal(0.5m * weight,
                result.RankedResults.Single(x => x.Symbol == "STRONG").RelativeStrengthScoreContribution);
            Assert.Equal(-0.5m * weight,
                result.RankedResults.Single(x => x.Symbol == "WEAK").RelativeStrengthScoreContribution);
        }
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_BenchmarkFailureDoesNotPreventStockAnalysis()
    {
        var start = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider(BuildCandles(start, 5));

        var result = await BuildWorkflow(provider).CollectAndAnalyzeAsync(["TEST"], start);

        var observation = Assert.Single(result.RankedResults);
        Assert.Null(observation.RelativeStrengthPercent);
        Assert.Equal(0m, observation.RelativeStrengthScoreContribution);
        Assert.Equal("SPY", observation.RelativeStrengthBenchmarkSymbol);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_PopulatesFundamentalData_WhenProviderPresent()
    {
        var date = new DateTime(2024, 1, 10);
        var candles = BuildCandles(date, 5);
        var marketProvider = new FakeMarketDataProvider(candles);
        var expectedFundamentals = new FundamentalData
        {
            Symbol = "TEST",
            RevenueGrowthPercent = 15m,
            EpsGrowthPercent = 10m,
            Margins = 20m,
            Debt = 0.5m
        };
        var fundamentalProvider = new FakeFundamentalDataProvider(
            new Dictionary<string, FundamentalData?>
            {
                ["TEST"] = expectedFundamentals
            });

        var workflow = BuildWorkflow(marketProvider, fundamentalDataProvider: fundamentalProvider);

        var result = await workflow.CollectAndAnalyzeAsync(["TEST"], date);

        var observation = Assert.Single(result.RankedResults);
        Assert.NotNull(observation.Fundamentals);
        Assert.Equal(15m, observation.Fundamentals.RevenueGrowthPercent);
        Assert.Equal(10m, observation.Fundamentals.EpsGrowthPercent);
        Assert.Equal(20m, observation.Fundamentals.Margins);
        Assert.Equal(0.5m, observation.Fundamentals.Debt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CollectAndAnalyzeAsync_ProviderFailureForOneStock_DoesNotBreakRankingFlow(bool progressEnabled)
    {
        var date = new DateTime(2024, 1, 10);
        var candlesFail = BuildCandles(date, 5);
        var candlesPass = BuildCandles(date, 5);
        candlesPass[^1].Close = 120m;

        var marketProvider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["FAIL"] = candlesFail,
                ["PASS"] = candlesPass
            });

        var passFundamentals = new FundamentalData
        {
            Symbol = "PASS",
            RevenueGrowthPercent = 25m
        };

        var fundamentalProvider = new CallbackFundamentalDataProvider(symbol =>
        {
            if (symbol == "FAIL")
            {
                throw new HttpRequestException("Simulated API failure");
            }

            return Task.FromResult<FundamentalData?>(passFundamentals);
        });

        var progress = progressEnabled ? new SimulationProgressRecorder() : null;
        var workflow = BuildWorkflow(marketProvider, fundamentalDataProvider: fundamentalProvider, progress: progress);
        using var output = new StringWriter();
        var originalOut = Console.Out;
        MarketDataResult result;
        try
        {
            Console.SetOut(output);
            result = await workflow.CollectAndAnalyzeAsync(["FAIL", "PASS"], date);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        Assert.Contains(
            "Failed to fetch fundamental data for FAIL (HttpRequestException). Continuing without fundamentals.",
            output.ToString());
        Assert.DoesNotContain("Simulated API failure", output.ToString());
        if (progress is not null)
        {
            Assert.Contains(progress.Reports,
                x => x.Message == "Failed to fetch fundamental data for FAIL (HttpRequestException). Continuing without fundamentals.");
            Assert.DoesNotContain(progress.Reports, x => x.Message.Contains("Simulated API failure"));
            Assert.Equal(0, progress.ActiveOperations);
        }

        Assert.Equal(2, result.RankedResults.Count);

        var passObservation = result.RankedResults.Single(r => r.Symbol == "PASS");
        Assert.NotNull(passObservation.Fundamentals);
        Assert.Equal(25m, passObservation.Fundamentals.RevenueGrowthPercent);

        var failObservation = result.RankedResults.Single(r => r.Symbol == "FAIL");
        Assert.Null(failObservation.Fundamentals);
    }

    [Fact]
    public async Task CollectAndAnalyzeAsync_MissingFundamentalDataForOneStock_DoesNotBreakRankingFlow()
    {
        var date = new DateTime(2024, 1, 10);
        var candlesA = BuildCandles(date, 5);
        var candlesB = BuildCandles(date, 5);

        var marketProvider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["A"] = candlesA,
                ["B"] = candlesB
            });

        var fundamentalProvider = new FakeFundamentalDataProvider(
            new Dictionary<string, FundamentalData?>
            {
                ["A"] = new FundamentalData { Symbol = "A", Debt = 1.0m },
                ["B"] = null
            });

        var workflow = BuildWorkflow(marketProvider, fundamentalDataProvider: fundamentalProvider);

        var result = await workflow.CollectAndAnalyzeAsync(["A", "B"], date);

        Assert.Equal(2, result.RankedResults.Count);
        Assert.NotNull(result.RankedResults.Single(r => r.Symbol == "A").Fundamentals);
        Assert.Null(result.RankedResults.Single(r => r.Symbol == "B").Fundamentals);
    }

    [Theory]
    [InlineData(QualityFilterAction.Exclude, QualityFilterAction.Neutral, false)]
    [InlineData(QualityFilterAction.Penalize, QualityFilterAction.Penalize, false)]
    [InlineData(QualityFilterAction.Exclude, QualityFilterAction.Exclude, true)]
    public async Task CollectAndAnalyzeAsync_QualityPolicyChangesRankingAndRecommendationsWithoutDroppingDiagnostics(
        QualityFilterAction failureAction, QualityFilterAction missingAction, bool providerThrows)
    {
        var date = new DateTime(2024, 1, 10);
        var candles = BuildCandles(date, 5);
        var marketProvider = new FakeMarketDataProvider(
            new Dictionary<string, IReadOnlyList<MarketCandle>>
            {
                ["PASS"] = candles, ["FAIL"] = candles, ["MISSING"] = candles
            });
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = false, UseQualityFilter = true,
            QualityMinimumRevenueGrowthPercent = 10m,
            QualityFailureAction = failureAction, QualityMissingDataAction = missingAction,
            QualityPenalty = 7m
        };
        var provider = new CallbackFundamentalDataProvider(symbol =>
        {
            if (symbol == "MISSING")
            {
                if (providerThrows)
                    throw new HttpRequestException("Simulated failure");
                return Task.FromResult<FundamentalData?>(null);
            }
            return Task.FromResult<FundamentalData?>(new FundamentalData
            {
                Symbol = symbol, RevenueGrowthPercent = symbol == "PASS" ? 10m : 9m
            });
        });

        var result = await BuildWorkflow(marketProvider, strategy, provider)
            .CollectAndAnalyzeAsync(["FAIL", "MISSING", "PASS"], date);

        Assert.Equal(3, result.RankedResults.Count);
        Assert.Equal(missingAction == QualityFilterAction.Neutral ? "MISSING" : "PASS",
            result.RankedResults[0].Symbol);
        var passingScore = result.RankedResults.Single(r => r.Symbol == "PASS").Score;
        foreach (var (symbol, action) in new[] { ("FAIL", failureAction), ("MISSING", missingAction) })
        {
            var observation = result.RankedResults.Single(r => r.Symbol == symbol);
            Assert.Equal(action == QualityFilterAction.Exclude ? StrategyConfiguration.DisqualifiedScore
                : passingScore - (action == QualityFilterAction.Penalize ? 7m : 0m), observation.Score);
            Assert.Equal(action != QualityFilterAction.Exclude, result.Recommendations.Any(r => r.Symbol == symbol));
            var factor = Assert.Single(observation.FactorContributions!, f => f.Factor == "QualityFilter");
            Assert.Contains(symbol == "FAIL" ? "Failed:" : "Missing:", factor.Explanation);
            Assert.Equal(observation.Score, observation.FactorContributions!.Sum(f => f.Contribution ?? 0m));
        }
    }

    private MarketDataWorkflow BuildWorkflow(
        IMarketDataProvider provider,
        StrategyConfiguration? strategy = null,
        IFundamentalDataProvider? fundamentalDataProvider = null,
        IWorkflowProgress? progress = null)
    {
        return new MarketDataWorkflow(
            provider,
            new MarketObservationAnalyzer(strategy),
            new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext),
            new FeatureSnapshotRepository(_dbContext),
            strategy,
            fundamentalDataProvider,
            progress: progress);
    }

    private static List<MarketCandle> BuildCandles(
        DateTime startDate,
        int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new MarketCandle
            {
                Date = startDate.AddDays(index),
                Open = 100m + index,
                High = 101m + index,
                Low = 99m + index,
                Close = 100m + index,
                Volume = 1_000_000m + index
            })
            .ToList();
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<MarketCandle>>
            _candlesBySymbol;

        public FakeMarketDataProvider(IReadOnlyList<MarketCandle> candles)
            : this(
                new Dictionary<string, IReadOnlyList<MarketCandle>>
                {
                    ["TEST"] = candles
                })
        {
        }

        public FakeMarketDataProvider(
            IReadOnlyDictionary<string, IReadOnlyList<MarketCandle>>
                candlesBySymbol) =>
            _candlesBySymbol = candlesBySymbol;

        public DateTime LastFrom { get; private set; }

        public List<string> Calls { get; } = [];
        public Action<string>? OnFetch { get; init; }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            LastFrom = from.Date;
            Calls.Add(symbol);
            OnFetch?.Invoke(symbol);

            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                _candlesBySymbol[symbol]
                    .Where(x => x.Date.Date >= from.Date)
                    .OrderBy(x => x.Date)
                    .ToList());
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
        {
            Calls.Add(symbol);
            OnFetch?.Invoke(symbol);
            return Task.FromResult<IReadOnlyList<MarketCandle>>(
                _candlesBySymbol[symbol]
                    .Where(x =>
                        x.Date.Date >= from.Date
                        && x.Date.Date <= to.Date)
                    .OrderBy(x => x.Date)
                    .ToList());
        }
    }

    private sealed class FakeFundamentalDataProvider(
        IReadOnlyDictionary<string, FundamentalData?> fundamentals)
        : IFundamentalDataProvider
    {
        public Task<FundamentalData?> GetFundamentalDataAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            fundamentals.TryGetValue(symbol, out var data);
            return Task.FromResult(data);
        }
    }

    private sealed class CallbackFundamentalDataProvider(
        Func<string, Task<FundamentalData?>> callback)
        : IFundamentalDataProvider
    {
        public Task<FundamentalData?> GetFundamentalDataAsync(
            string symbol,
            CancellationToken cancellationToken = default)
        {
            return callback(symbol);
        }
    }
}
