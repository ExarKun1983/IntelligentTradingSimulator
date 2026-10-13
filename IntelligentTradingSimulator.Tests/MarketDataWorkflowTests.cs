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

    [Fact]
    public async Task CollectAndAnalyzeAsync_ProviderFailureForOneStock_DoesNotBreakRankingFlow()
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

        var workflow = BuildWorkflow(marketProvider, fundamentalDataProvider: fundamentalProvider);

        using var writer = new StringWriter();
        var originalOut = Console.Out;
        MarketDataResult result;
        try
        {
            Console.SetOut(writer);
            result = await workflow.CollectAndAnalyzeAsync(["FAIL", "PASS"], date);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains(
            "Failed to fetch fundamental data for FAIL (HttpRequestException). Continuing without fundamentals.",
            writer.ToString());
        Assert.DoesNotContain("Simulated API failure", writer.ToString());

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
        IFundamentalDataProvider? fundamentalDataProvider = null)
    {
        return new MarketDataWorkflow(
            provider,
            new MarketObservationAnalyzer(strategy),
            new PortfolioRecommendationService(),
            new ObservationResultRepository(_dbContext),
            strategy,
            fundamentalDataProvider);
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

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            LastFrom = from.Date;
            Calls.Add(symbol);

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
