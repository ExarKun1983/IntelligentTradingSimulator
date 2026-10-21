using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

[Collection("Console output")]
public sealed class BacktestDayProcessorTests : IDisposable
{
    private readonly DbContextOptions<TradingSimulatorDbContext> _options;
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestSnapshotRepository _snapshotRepository;

    public BacktestDayProcessorTests()
    {
        _options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(_options);
        _snapshotRepository = new BacktestSnapshotRepository(_dbContext);
    }

    public void Dispose() => _dbContext.Dispose();

    private BacktestDayProcessor BuildProcessor(
        FakeMarketDataProvider provider,
        int maxPositions = 10,
        decimal maxPositionWeight = 100m,
        IWorkflowProgress? progress = null,
        IBacktestSnapshotRepository? snapshotRepository = null,
        IFeatureSnapshotRepository? featureSnapshotRepository = null,
        IMarketCandleRepository? marketCandleRepository = null)
    {
        var historicalService = new BacktestHistoricalMarketDataService(provider, marketCandleRepository);
        var analyzerFactory = new MarketObservationAnalyzerFactory();
        var recommendationService =
            new PortfolioRecommendationService(maxPositions);
        var allocationService = new PortfolioAllocationService();
        var allocationEngine = new PortfolioAllocationEngine(
            allocationService,
            maxPositionWeight);
        var tradingEngine = new TradingEngine();
        var rebalancingService = new PortfolioRebalancingService();
        var performanceService = new PortfolioPerformanceService();

        return new BacktestDayProcessor(
            historicalService,
            analyzerFactory,
            recommendationService,
            rebalancingService,
            allocationEngine,
            tradingEngine,
            performanceService,
            snapshotRepository ?? _snapshotRepository,
            featureSnapshotRepository ?? new FeatureSnapshotRepository(_dbContext),
            progress);
    }

    [Fact]
    public async Task ProcessAsync_ConfirmedWeekendTailKeepsPersistedSymbolAndBenchmarkAvailable()
    {
        var monday = new DateTime(2024, 1, 1);
        var friday = monday.AddDays(4);
        var saturday = monday.AddDays(5);
        var repository = new MarketCandleRepository(_dbContext);
        foreach (var symbol in new[] { "AAPL", "SPY" })
        {
            await repository.UpsertAsync(symbol, HistoricalMarketDataPolicy.Source,
                HistoricalMarketDataPolicy.PriceAdjustmentMode,
                MarketHistoryRequirements.GetEffectiveStartDate(monday, saturday),
                friday, BuildCandles(monday, 5));
        }
        var provider = new FakeMarketDataProvider();
        var processor = BuildProcessor(provider, marketCandleRepository: repository);
        using var output = new System.IO.StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            for (var day = 0; day < 2; day++)
            {
                var result = await processor.ProcessAsync(new BacktestDayRequest
                {
                    CurrentDate = saturday.AddDays(day), HistoricalStartDate = monday,
                    Portfolio = new Portfolio { StartingCash = 1000m, Cash = 1000m },
                    StrategyConfiguration = new StrategyConfiguration
                    {
                        UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "SPY"
                    },
                    Symbols = ["AAPL"]
                });
                var observation = Assert.Single(result.Observations);
                Assert.Equal("AAPL", observation.Symbol);
                Assert.Equal(0m, observation.RelativeStrengthPercent);
            }
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        Assert.DoesNotContain("skipping", output.ToString());
        Assert.DoesNotContain("benchmark data unavailable", output.ToString());
        Assert.Equal(10, await _dbContext.MarketCandles.CountAsync());
    }

    private static List<MarketCandle> BuildCandles(
        DateTime startDate,
        int count,
        decimal startPrice = 100m)
    {
        return Enumerable.Range(0, count)
            .Select(i => new MarketCandle
            {
                Date = startDate.AddDays(i),
                Open = startPrice + i,
                High = startPrice + i + 1m,
                Low = startPrice + i - 1m,
                Close = startPrice + i,
                Volume = 1_000_000m
            })
            .ToList();
    }

    [Theory]
    [InlineData(TradeActionType.Buy)]
    [InlineData(TradeActionType.Sell)]
    public async Task ProcessAsync_TradesUseSimulatedDayTimestamp(TradeActionType action)
    {
        var startDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var simulatedDate = startDate.AddDays(9);
        var provider = new FakeMarketDataProvider();
        provider.SetData("AAPL", BuildCandles(startDate, 10));
        var portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m };
        if (action == TradeActionType.Sell)
        {
            portfolio.Cash = 0m;
            portfolio.Positions.Add(new Position
            {
                Symbol = "AAPL", Quantity = 100m, AveragePrice = 100m, CurrentPrice = 109m
            });
        }

        var result = await BuildProcessor(provider, maxPositionWeight: 50m).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = simulatedDate,
            HistoricalStartDate = startDate,
            Portfolio = portfolio,
            StrategyConfiguration = new StrategyConfiguration { UseRelativeStrength = false },
            Symbols = ["AAPL"]
        });

        var trade = Assert.Single(result.Portfolio.Trades);
        Assert.Equal(action, trade.Action);
        Assert.Equal(simulatedDate, trade.Timestamp);
        Assert.Equal(1, result.Snapshot.TradesExecuted);
    }

    [Fact]
    public async Task ProcessAsync_WithoutProgressPreservesBenchmarkAndSymbolWarnings()
    {
        var date = new DateTime(2024, 1, 10);
        var provider = new FakeMarketDataProvider
        {
            ThrowForSymbol = "SPY",
            OnFetch = symbol =>
            {
                if (symbol == "FAIL")
                    throw new InvalidOperationException("Price unavailable.");
            }
        };
        using var output = new System.IO.StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
            {
                CurrentDate = date, HistoricalStartDate = date.AddDays(-10),
                Portfolio = new Portfolio { StartingCash = 1000m, Cash = 1000m },
                StrategyConfiguration = new StrategyConfiguration
                {
                    UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "SPY"
                },
                Symbols = ["FAIL"]
            });
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("Backtest: benchmark data unavailable for SPY.", output.ToString());
        Assert.Contains("Backtest: skipping FAIL on 2024-01-10 — Price unavailable.", output.ToString());
        Assert.DoesNotContain("Symbols processed:", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_ConsoleProgressWarnsOncePerRunForPartialDataAndUnavailableBenchmark(bool benchmarkThrows)
    {
        var date = new DateTime(2024, 1, 10);
        using var output = new System.IO.StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, interval: TimeSpan.FromDays(1));
        var provider = new FakeMarketDataProvider { ThrowForSymbol = benchmarkThrows ? "SPY" : null };
        provider.SetData("PASS", BuildCandles(date.AddDays(-4), 6));
        provider.SetData("SHORT", BuildCandles(date, 1));
        var processor = BuildProcessor(provider, progress: progress);

        foreach (var runId in new[] { 7, 8 })
        {
            for (var day = 0; day < 2; day++)
            {
                await processor.ProcessAsync(new BacktestDayRequest
                {
                    BacktestRunId = runId, CurrentDate = date.AddDays(day),
                    HistoricalStartDate = date.AddDays(-4),
                    Portfolio = new Portfolio { StartingCash = 1000m, Cash = 1000m },
                    StrategyConfiguration = new StrategyConfiguration
                    {
                        UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "SPY"
                    },
                    Symbols = ["PASS", "SHORT"]
                });
            }
        }

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var partialWarnings = lines.Where(x => x.StartsWith("Partial market data:")).ToList();
        var benchmarkWarnings = lines.Where(x => x.StartsWith("Benchmark data unavailable for SPY")).ToList();
        Assert.Equal(2, partialWarnings.Count);
        Assert.Equal(2, benchmarkWarnings.Count);
        Assert.All(partialWarnings, line =>
            Assert.Contains("1 analyzed, 1 skipped, 0 failed", line));
        for (var index = 0; index < 2; index++)
        {
            Assert.Contains($"Run: {7 + index}", partialWarnings[index]);
            Assert.Contains($"Run: {7 + index}", benchmarkWarnings[index]);
        }
        var expectedCalls = new[] { "SPY", "PASS", "SHORT", "SPY", "PASS", "SHORT" };
        Assert.Equal(benchmarkThrows ? expectedCalls.Concat(["SPY", "SPY"]) : expectedCalls, provider.Calls);
        Assert.Equal(4, await _dbContext.BacktestSnapshots.CountAsync());
    }

    [Fact]
    public async Task ProcessAsync_IntentionalUniverseExclusionDoesNotConsumePartialDataWarning()
    {
        var date = new DateTime(2024, 1, 10);
        var progress = new SimulationProgressRecorder();
        var provider = new FakeMarketDataProvider();
        provider.SetData("PASS", BuildCandles(date.AddDays(-5), 6));
        provider.SetData("HELD", BuildCandles(date.AddDays(-5), 6));
        provider.SetData("SHORT", BuildCandles(date, 1));
        var processor = BuildProcessor(provider, progress: progress);
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = false,
            StopLossPercent = 10m
        };

        await processor.ProcessAsync(new BacktestDayRequest
        {
            BacktestRunId = 17,
            CurrentDate = date,
            HistoricalStartDate = date.AddDays(-5),
            Portfolio = new Portfolio
            {
                StartingCash = 1000m,
                Cash = 1000m,
                Positions = [new Position
                {
                    Symbol = "HELD", Quantity = 1m, AveragePrice = 100m, CurrentPrice = 100m
                }]
            },
            StrategyConfiguration = strategy,
            Symbols = ["PASS"]
        });

        Assert.DoesNotContain(progress.Reports, x => x.Message.StartsWith("Partial market data:"));

        await processor.ProcessAsync(new BacktestDayRequest
        {
            BacktestRunId = 17,
            CurrentDate = date.AddDays(1),
            HistoricalStartDate = date.AddDays(-5),
            Portfolio = new Portfolio
            {
                StartingCash = 1000m,
                Cash = 1000m,
                Positions = [new Position
                {
                    Symbol = "HELD", Quantity = 1m, AveragePrice = 100m, CurrentPrice = 100m
                }]
            },
            StrategyConfiguration = strategy,
            Symbols = ["SHORT"]
        });

        var warning = Assert.Single(progress.Reports
            .Where(x => x.Message.StartsWith("Partial market data:")));
        Assert.Contains("0 analyzed, 1 skipped, 0 failed", warning.Message);
        Assert.True(warning.Force);
    }

    [Fact]
    public async Task ProcessAsync_ProgressScopesCachedRetrievalsAndCountsEverySymbolOutcome()
    {
        var date = new DateTime(2024, 1, 10);
        var progress = new SimulationProgressRecorder();
        var activeDuringFetch = new List<int>();
        var provider = new FakeMarketDataProvider
        {
            ThrowForSymbol = "FAIL",
            OnFetch = _ => activeDuringFetch.Add(progress.ActiveOperations)
        };
        provider.SetData("PASS", BuildCandles(date.AddDays(-4), 5));
        provider.SetData("SKIP", BuildCandles(date, 1));
        var processor = BuildProcessor(provider, progress: progress);
        var request = new BacktestDayRequest
        {
            BacktestRunId = 7, CurrentDate = date, HistoricalStartDate = date.AddDays(-4),
            Portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m },
            StrategyConfiguration = new StrategyConfiguration
            {
                Id = "backtest-strategy", UseRelativeStrength = true, RelativeStrengthBenchmarkSymbol = "SPY"
            },
            Symbols = ["PASS", "SKIP", "FAIL"]
        };

        var result = await processor.ProcessAsync(request);
        await processor.ProcessAsync(request);

        Assert.Single(result.Observations);
        Assert.Equal(new[] { "SPY", "PASS", "SKIP", "FAIL", "FAIL" }, provider.Calls);
        Assert.All(activeDuringFetch, count => Assert.Equal(1, count));
        Assert.Equal(6, progress.Operations.Count(x => x.Operation == "Fetching price data"));
        Assert.Equal(2, progress.Operations.Count(x => x.Operation == "Fetching benchmark data"));
        Assert.Contains(progress.Operations, x => x.Operation == "Building rankings");
        Assert.Contains(progress.Operations, x => x.Operation == "Saving feature snapshots");
        Assert.Contains(progress.Operations, x => x.Operation == "Processing portfolio");
        Assert.Contains(progress.Operations, x => x.Operation == "Saving daily snapshot");
        Assert.Equal(7, progress.Context.RunId);
        Assert.Equal(date, progress.Context.Date);
        Assert.Equal("backtest-strategy", progress.Context.StrategyId);
        Assert.Equal(3, progress.Context.TotalSymbols);
        Assert.Equal(3, progress.Context.CompletedSymbols);
        Assert.Equal(new int?[] { 1, 2, 3, 1, 2, 3 },
            progress.Reports.Where(x => x.Message.StartsWith("Symbols processed:"))
                .Select(x => x.Context.CompletedSymbols));
        Assert.Equal("Symbols processed: 1 analyzed, 1 skipped, 1 failed", progress.Reports.Last().Message);
        Assert.Equal(0, progress.ActiveOperations);
        Assert.Equal(2, await _dbContext.BacktestSnapshots.CountAsync());
    }

    [Theory]
    [InlineData(ScoringEngineType.Composite, false)]
    [InlineData(ScoringEngineType.Composite, true)]
    [InlineData(ScoringEngineType.RuleBased, false)]
    [InlineData(ScoringEngineType.RuleBased, true)]
    [InlineData(ScoringEngineType.AI, false)]
    [InlineData(ScoringEngineType.AI, true)]
    public async Task ProcessAsync_PersistsAllAnalyzedStocks_WithRunContextAndNoFutureData(
        ScoringEngineType scoringEngine, bool progressEnabled)
    {
        var date = new DateTime(2024, 1, 10);
        using var output = new System.IO.StringWriter();
        using var progress = progressEnabled
            ? new ConsoleWorkflowProgress(output, interval: TimeSpan.FromDays(1))
            : null;
        var provider = new FakeMarketDataProvider();
        var candles = BuildCandles(date.AddDays(-4), 6);
        candles[^1].Close = 1000m;
        provider.SetData("AAPL", candles);
        provider.SetData("MSFT", candles);
        provider.SetData("SHORT", BuildCandles(date, 1));
        var strategy = new StrategyConfiguration
        {
            Id = "historical", UseRelativeStrength = false, ScoringEngine = scoringEngine
        };
        var repositories = new CountingBacktestRepositories(_dbContext);

        var result = await BuildProcessor(provider, maxPositions: 1, progress: progress,
            snapshotRepository: repositories, featureSnapshotRepository: repositories).ProcessAsync(new BacktestDayRequest
        {
            BacktestRunId = 10, CurrentDate = date, HistoricalStartDate = date.AddDays(-4),
            Symbols = ["AAPL", "MSFT", "SHORT"], StrategyConfiguration = strategy,
            Portfolio = new Portfolio { Cash = 1000m, StartingCash = 1000m }
        });

        Assert.Equal(new[] { "AAPL", "MSFT", "SHORT" }, provider.Calls);
        Assert.Equal(new[] { "SaveFeatures", "AddSnapshot" }, repositories.Calls);
        if (progressEnabled)
        {
            Assert.Contains("Symbols: 3/3", output.ToString());
            Assert.Contains("Starting Building rankings", output.ToString());
            Assert.DoesNotContain("SHORT", output.ToString());
            Assert.DoesNotContain("za mało", output.ToString());
        }
        else
        {
            Assert.Equal("", output.ToString());
        }
        await using var readContext = new TradingSimulatorDbContext(_options);
        var snapshots = await new FeatureSnapshotRepository(readContext).QueryAsync(
            new DateOnly(2024, 1, 10), new DateOnly(2024, 1, 10), strategyId: "historical", backtestRunId: 10);
        Assert.Equal(2, snapshots.Count);
        Assert.Single(result.Recommendations);
        Assert.All(snapshots, snapshot =>
        {
            Assert.Equal(4m, snapshot.Features.MomentumPercent);
            Assert.Null(snapshot.Features.Fundamentals);
            Assert.Equal(result.Observations.Single(x => x.Symbol == snapshot.Symbol).Score, snapshot.Score);
            Assert.Equal(new DateOnly(2024, 1, 10), snapshot.Features.AnalysisDate);
        });
        Assert.DoesNotContain(snapshots, x => x.Symbol == "SHORT");
        var ranking = new MarketObservationAnalyzer(ScoringEngineFactory.Create(strategy))
            .Analyze("AAPL", candles.Take(5).ToArray());
        var backtest = result.Observations.Single(x => x.Symbol == "AAPL");
        Assert.Equal(ranking.Score, backtest.Score);
        Assert.Equal(ranking.FactorContributions, backtest.FactorContributions);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(91, false)]
    public async Task ProcessAsync_StopLossUsesAvailableCloseWithoutScoringHistoryOrLookahead(
        int currentPrice, bool shouldSell)
    {
        var date = new DateTime(2024, 1, 10);
        var provider = new FakeMarketDataProvider();
        provider.SetData("HELD",
        [
            new MarketCandle { Date = date, Close = currentPrice },
            new MarketCandle { Date = date.AddDays(1), Close = 1m }
        ]);
        var portfolio = new Portfolio
        {
            StartingCash = 1000m,
            Positions = [new Position
            {
                Symbol = "HELD", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 100m
            }]
        };

        var result = await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = date,
            HistoricalStartDate = date.AddDays(-10),
            Portfolio = portfolio,
            Symbols = [],
            StrategyConfiguration = new StrategyConfiguration
            {
                UseRelativeStrength = false, StopLossPercent = 10m
            }
        });

        Assert.Empty(result.Recommendations);
        Assert.Equal(shouldSell ? 1 : 0, result.Snapshot.TradesExecuted);
        Assert.False(result.Snapshot.RebalanceExecuted);
        Assert.Equal(currentPrice * 10m, result.Snapshot.PortfolioValue);
        if (shouldSell)
        {
            Assert.Empty(portfolio.Positions);
            var sell = Assert.Single(portfolio.Trades);
            Assert.Equal(currentPrice, sell.Price);
            Assert.Equal(10m, sell.Quantity);
            Assert.Equal(ExitReason.StopLoss, sell.ExitReason);
            Assert.Equal(date, sell.Timestamp);
        }
        else
        {
            Assert.Empty(portfolio.Trades);
            Assert.Equal(currentPrice, Assert.Single(portfolio.Positions).CurrentPrice);
        }
    }

    [Theory]
    [InlineData(108, true)]
    [InlineData(109, false)]
    public async Task ProcessAsync_TrailingStopUsesHeldPositionHighWithoutScoringHistoryOrLookahead(
        int price, bool shouldSell)
    {
        var date = new DateTime(2024, 1, 10);
        var provider = new FakeMarketDataProvider();
        provider.SetData("HELD",
        [
            new MarketCandle { Date = date.AddDays(-1), Close = 1000m },
            new MarketCandle { Date = date, Close = price },
            new MarketCandle { Date = date.AddDays(1), Close = 2000m }
        ]);
        var position = new Position
        {
            Symbol = "HELD", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 120m
        };
        var portfolio = new Portfolio { StartingCash = 1000m, Positions = [position] };
        var result = await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = date,
            HistoricalStartDate = date.AddDays(-10),
            Portfolio = portfolio,
            Symbols = [],
            StrategyConfiguration = new StrategyConfiguration
            {
                UseRelativeStrength = false, TrailingStopPercent = 10m
            }
        });

        Assert.Empty(result.Observations);
        Assert.Empty(result.Recommendations);
        Assert.Equal(120m, position.HighestPriceSinceEntry);
        Assert.Equal(shouldSell ? 1 : 0, result.Snapshot.TradesExecuted);
        Assert.False(result.Snapshot.RebalanceExecuted);
        Assert.Equal(price * 10m, result.Snapshot.PortfolioValue);
        if (shouldSell)
        {
            Assert.Empty(portfolio.Positions);
            var sell = Assert.Single(portfolio.Trades);
            Assert.Equal(ExitReason.TrailingStop, sell.ExitReason);
            Assert.Equal(date, sell.Timestamp);
            Assert.Equal(price, sell.Price);
            Assert.Equal(10m, sell.Quantity);
        }
        else
        {
            Assert.Empty(portfolio.Trades);
            Assert.Equal(price, Assert.Single(portfolio.Positions).CurrentPrice);
        }
    }

    [Fact]
    public async Task RunAsync_PersistsTrailingStopExitAfterNewHigh_InFreshContext()
    {
        var startDate = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        var candles = BuildCandles(startDate, 12);
        candles[^2].Close = 150m;
        candles[^1].Close = 135m;
        provider.SetData("AAPL", candles);
        var engine = new BacktestEngine(
            new BacktestRunRepository(_dbContext), BuildProcessor(provider));

        var result = await engine.RunAsync(new BacktestEngineRequest
        {
            Name = "Trailing Stop persistence",
            StrategyId = "test",
            StrategyConfiguration = new StrategyConfiguration
            {
                Id = "test", UseRelativeStrength = false, TrailingStopPercent = 10m
            },
            StartDate = startDate.AddDays(9),
            EndDate = startDate.AddDays(11),
            HistoricalStartDate = startDate,
            InitialCapital = 10_000m,
            Symbols = ["AAPL"]
        });

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        await _dbContext.DisposeAsync();
        await using var readContext = new TradingSimulatorDbContext(_options);
        var savedRun = await new BacktestRunRepository(readContext).GetByIdAsync(result.RunId);
        Assert.NotNull(savedRun?.Portfolio);
        Assert.Empty(savedRun.Portfolio.Positions);
        var sell = Assert.Single(savedRun.Portfolio.Trades,
            x => x.ExitReason == ExitReason.TrailingStop);
        Assert.Equal(135m, sell.Price);
        Assert.True(sell.Price > savedRun.Portfolio.Trades.First(x => x.Action == TradeActionType.Buy).Price);
        Assert.Single(await readContext.Trades.AsNoTracking()
            .Where(x => x.PortfolioId == savedRun.PortfolioId && x.ExitReason == ExitReason.TrailingStop)
            .ToListAsync());
        Assert.Equal(savedRun.FinalCapital, Math.Round(savedRun.Portfolio.Cash, 4));
    }

    [Fact]
    public async Task ProcessAsync_StopLossDoesNotAddHeldSymbolsToRecommendationUniverse()
    {
        var date = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        provider.SetData("HELD", BuildCandles(date, 10));
        var portfolio = new Portfolio
        {
            StartingCash = 1000m,
            Positions = [new Position
            {
                Symbol = "HELD", Quantity = 10m, AveragePrice = 100m, CurrentPrice = 100m
            }]
        };

        var result = await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = date.AddDays(9),
            HistoricalStartDate = date,
            Portfolio = portfolio,
            Symbols = [],
            StrategyConfiguration = new StrategyConfiguration
            {
                UseRelativeStrength = false, StopLossPercent = 10m
            }
        });

        Assert.Empty(result.Observations);
        Assert.Empty(result.Recommendations);
        Assert.Empty(portfolio.Trades);
        Assert.Equal(109m, Assert.Single(portfolio.Positions).CurrentPrice);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(true, "Starting Fetching price data", false)]
    [InlineData(true, "Starting Fetching price data", true)]
    [InlineData(true, "Starting Saving final backtest result", false)]
    [InlineData(true, "Starting Saving final backtest result", true)]
    public async Task RunAsync_PersistsAutomaticStopLossExit_InFreshContext(
        bool progressEnabled, string? failingMessage, bool objectDisposed)
    {
        var startDate = new DateTime(2024, 1, 1);
        using var output = failingMessage is null
            ? new System.IO.StringWriter()
            : new FailOnceProgressWriter(failingMessage, objectDisposed);
        using var progress = progressEnabled
            ? new ConsoleWorkflowProgress(output, interval: TimeSpan.FromDays(1))
            : null;
        var provider = new FakeMarketDataProvider();
        var candles = BuildCandles(startDate, 11);
        candles[^1].Close = 90m;
        provider.SetData("AAPL", candles);
        var repositories = new CountingBacktestRepositories(_dbContext);
        var engine = new BacktestEngine(
            repositories,
            BuildProcessor(provider, progress: progress,
                snapshotRepository: repositories, featureSnapshotRepository: repositories),
            progress);

        var result = await engine.RunAsync(new BacktestEngineRequest
        {
            Name = "Stop Loss persistence",
            StrategyId = "test",
            StrategyConfiguration = new StrategyConfiguration
            {
                Id = "test", UseRelativeStrength = false, StopLossPercent = 10m
            },
            StartDate = startDate.AddDays(9),
            EndDate = startDate.AddDays(10),
            HistoricalStartDate = startDate,
            InitialCapital = 10_000m,
            Symbols = ["AAPL"]
        });

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        Assert.Equal(new[] { "AAPL", "AAPL" }, provider.Calls);
        Assert.Equal(
            new[] { "AddRun", "SaveFeatures", "AddSnapshot", "SaveFeatures", "AddSnapshot", "UpdateRun" },
            repositories.Calls);
        if (output is FailOnceProgressWriter failingOutput)
        {
            Assert.True(failingOutput.Failed);
        }
        else if (progressEnabled)
        {
            Assert.Contains("Starting Fetching price data", output.ToString());
            Assert.Contains("Simulation complete", output.ToString());
            Assert.Contains("Completed calendar dates: 2/2", output.ToString());
            Assert.DoesNotContain("Workflow complete", output.ToString());
        }
        else
        {
            Assert.Equal("", output.ToString());
        }
        await _dbContext.DisposeAsync();
        await using var readContext = new TradingSimulatorDbContext(_options);
        var savedRun = await new BacktestRunRepository(readContext).GetByIdAsync(result.RunId);
        Assert.NotNull(savedRun?.Portfolio);
        Assert.Equal(BacktestRunStatus.Completed, savedRun.Status);
        Assert.Equal(result.FinalCapital, savedRun.FinalCapital);
        Assert.Equal(result.TotalProfit, savedRun.TotalProfit);
        Assert.Equal(result.TotalReturnPercent, savedRun.TotalReturnPercent);
        Assert.Empty(savedRun.Portfolio.Positions);
        var buy = Assert.Single(savedRun.Portfolio.Trades, x => x.Action == TradeActionType.Buy);
        var sell = Assert.Single(savedRun.Portfolio.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(startDate.AddDays(9), buy.Timestamp);
        Assert.Equal(startDate.AddDays(10), sell.Timestamp);
        Assert.Equal(109m, buy.Price);
        Assert.Equal(90m, sell.Price);
        Assert.Equal(buy.Quantity, sell.Quantity);
        Assert.Equal(ExitReason.StopLoss, sell.ExitReason);
        Assert.Equal(new[] { TradeActionType.Buy, TradeActionType.Sell },
            savedRun.Portfolio.Trades.Select(trade => trade.Action));
        var expectedQuantity = Math.Round(10_000m / 109m, 4, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedQuantity, buy.Quantity);
        var expectedFinalCapital = Math.Round(expectedQuantity * 90m, 4);
        Assert.Equal(expectedFinalCapital, result.FinalCapital);
        Assert.Equal(expectedFinalCapital - 10_000m, result.TotalProfit);
        Assert.Equal(savedRun.FinalCapital, Math.Round(savedRun.Portfolio.Cash, 4));
        Assert.Single(await readContext.Trades.AsNoTracking()
            .Where(x => x.PortfolioId == savedRun.PortfolioId && x.ExitReason == ExitReason.StopLoss)
            .ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PersistsRebalanceExitReason_InFreshContext(bool hasNormalPortfolio)
    {
        var startDate = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        var candles = BuildCandles(startDate, 11);
        candles[^1].Open = 120m;
        candles[^1].High = 121m;
        candles[^1].Low = 119m;
        candles[^1].Close = 120m;
        provider.SetData("AAPL", candles);
        var normalPortfolio = new Portfolio { Name = "Normal", StartingCash = 1_000m, Cash = 1_000m };
        if (hasNormalPortfolio)
        {
            await new PortfolioRepository(_dbContext).SaveAsync(normalPortfolio);
        }
        var engine = new BacktestEngine(
            new BacktestRunRepository(_dbContext),
            BuildProcessor(provider, maxPositions: 1, maxPositionWeight: 50m));

        var result = await engine.RunAsync(new BacktestEngineRequest
        {
            Name = "Exit reason persistence",
            StrategyId = "test",
            StrategyConfiguration = new StrategyConfiguration { Id = "test", UseRelativeStrength = false },
            StartDate = startDate.AddDays(9),
            EndDate = startDate.AddDays(10),
            HistoricalStartDate = startDate,
            InitialCapital = 10_000m,
            Symbols = ["AAPL"]
        });

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        await _dbContext.DisposeAsync();
        await using var readContext = new TradingSimulatorDbContext(_options);
        var savedRun = await new BacktestRunRepository(readContext).GetByIdAsync(result.RunId);

        Assert.NotNull(savedRun);
        Assert.NotNull(savedRun.Portfolio);
        var sell = Assert.Single(savedRun.Portfolio.Trades, x => x.Action == TradeActionType.Sell);
        Assert.Equal(ExitReason.Rebalance, sell.ExitReason);
        Assert.Equal(120m, sell.Price);
        Assert.True(sell.Quantity > 0m);
        Assert.All(savedRun.Portfolio.Trades.Where(x => x.Action == TradeActionType.Buy),
            trade => Assert.Null(trade.ExitReason));
        var persistedSell = Assert.Single(await readContext.Trades
            .AsNoTracking()
            .Where(x => x.PortfolioId == savedRun.PortfolioId && x.ExitReason == ExitReason.Rebalance)
            .ToListAsync());
        Assert.Equal(sell.Id, persistedSell.Id);

        var portfolioRepository = new PortfolioRepository(readContext);
        if (hasNormalPortfolio)
        {
            Assert.Equal(normalPortfolio.Id, (await portfolioRepository.GetLatestAsync())!.Id);
            Assert.Equal(normalPortfolio.Id, Assert.Single(await portfolioRepository.GetAllAsync()).Id);
        }
        else
        {
            Assert.Null(await portfolioRepository.GetLatestAsync());
            Assert.Empty(await portfolioRepository.GetAllAsync());
        }
    }

    [Fact]
    public async Task ProcessAsync_CompositeWeightsCanChangeMultiStockRanking()
    {
        var startDate = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        var momentumCandles = BuildCandles(startDate, 10, 100m);
        var volumeCandles = BuildCandles(startDate, 10, 200m);
        volumeCandles[^1].Volume = 2_000_000m;
        provider.SetData("MOMENTUM", momentumCandles);
        provider.SetData("VOLUME", volumeCandles);
        var processor = BuildProcessor(provider);

        async Task<BacktestDayResult> Run(StrategyConfiguration configuration) =>
            await processor.ProcessAsync(new BacktestDayRequest
            {
                BacktestRunId = 32,
                CurrentDate = startDate.AddDays(9),
                HistoricalStartDate = startDate,
                Portfolio = new Portfolio { Name = "Composite", StartingCash = 10_000m, Cash = 10_000m },
                Symbols = ["MOMENTUM", "VOLUME"],
                StrategyConfiguration = configuration
            });

        var baseline = await Run(new StrategyConfiguration { UseRelativeStrength = false });
        var volumeWeighted = await Run(new StrategyConfiguration
        {
            MomentumScoreWeight = 0m,
            VolumeTrendScoreWeight = 1m,
            VolatilityScoreWeight = 0m,
            UseRelativeStrength = false
        });

        Assert.Equal("MOMENTUM", baseline.Observations[0].Symbol);
        Assert.Equal("VOLUME", volumeWeighted.Observations[0].Symbol);
        Assert.Equal(105m, volumeWeighted.Observations[0].Score);
        Assert.Equal(5m, volumeWeighted.Observations[1].Score);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProcessAsync_AndMarketWorkflow_UseSameCompositeRankingAndOrdinalTies(
        bool volumeWeighted, bool reverseInput)
    {
        var startDate = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        var momentumCandles = BuildCandles(startDate, 10, 100m);
        var volumeCandles = BuildCandles(startDate, 10, 200m);
        volumeCandles[^1].Volume = 2_000_000m;
        var disqualifiedCandles = BuildCandles(startDate, 10);
        disqualifiedCandles[^1].Close = 90m;
        provider.SetData("MOMENTUM", momentumCandles);
        provider.SetData("Z_VOLUME", volumeCandles);
        provider.SetData("A_VOLUME", volumeCandles);
        provider.SetData("DISQUALIFIED", disqualifiedCandles);
        var strategy = new StrategyConfiguration
        {
            UseRelativeStrength = false,
            MomentumScoreWeight = volumeWeighted ? 1m : StrategyConfiguration.DefaultMomentumScoreWeight,
            VolumeTrendScoreWeight = volumeWeighted ? 1m : StrategyConfiguration.DefaultVolumeTrendScoreWeight,
            VolatilityScoreWeight = volumeWeighted ? 0m : StrategyConfiguration.DefaultVolatilityScoreWeight
        };
        string[] symbols = ["Z_VOLUME", "A_VOLUME", "MOMENTUM", "DISQUALIFIED"];
        if (reverseInput)
        {
            Array.Reverse(symbols);
        }
        string[] expected = volumeWeighted
            ? ["A_VOLUME", "Z_VOLUME", "MOMENTUM", "DISQUALIFIED"]
            : ["MOMENTUM", "A_VOLUME", "Z_VOLUME", "DISQUALIFIED"];

        var backtest = await BuildProcessor(provider, maxPositions: 2).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            StrategyConfiguration = strategy,
            Portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m },
            Symbols = symbols
        });
        var repository = new ObservationResultRepository(_dbContext);

        // Persist unsorted observations to verify saved rank positions use the same tie-breaker.
        await repository.SaveAsync(backtest.Observations.Reverse().ToArray());
        var saved = await repository.GetLatestRunAsync();
        Assert.NotNull(saved);
        Assert.Equal(expected, saved.Results.OrderBy(x => x.RankPosition).Select(x => x.Symbol).ToArray());

        var workflow = new MarketDataWorkflow(
            provider,
            new MarketObservationAnalyzer(strategy),
            new PortfolioRecommendationService(maxPositions: 2),
            repository,
            new FeatureSnapshotRepository(_dbContext),
            strategy);
        var market = await workflow.CollectAndAnalyzeAsync(symbols, startDate);

        Assert.Equal(expected, backtest.Observations.Select(x => x.Symbol).ToArray());
        Assert.Equal(expected, market.RankedResults.Select(x => x.Symbol).ToArray());
        Assert.Equal(expected.Take(2), backtest.Recommendations.Select(x => x.Symbol));
        Assert.Equal(expected.Take(2), market.Recommendations.Select(x => x.Symbol));
        Assert.Equal(
            market.Recommendations.Select(x => (x.Score, x.AllocationPercent)),
            backtest.Recommendations.Select(x => (x.Score, x.AllocationPercent)));
        Assert.Equal(
            expected.Take(2).OrderBy(x => x, StringComparer.Ordinal),
            backtest.Portfolio.Positions.Select(x => x.Symbol).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(new[] { backtest.Observations, market.RankedResults }, observations =>
        {
            var volume = observations.Single(x => x.Symbol == "A_VOLUME");
            Assert.Equal(4.5m, volume.MomentumPercent);
            Assert.Equal(100m, volume.VolumeTrendPercent);
            Assert.Equal(1_100_000m, volume.AverageVolume);
            Assert.True(volume.VolatilityPercent > 0m);
            Assert.Equal(9m, observations.Single(x => x.Symbol == "MOMENTUM").MomentumPercent);
            Assert.Equal(StrategyConfiguration.DisqualifiedScore, observations[^1].Score);
            Assert.All(observations, observation =>
            {
                Assert.NotNull(observation.FactorContributions);
                Assert.Equal(observation.Score, observation.FactorContributions.Sum(x => x.Contribution ?? 0m));
            });
        });
    }

    [Fact]
    public async Task ProcessAsync_CreatesAndPersistsSnapshot()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 1, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 150m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 300m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 1,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result.Snapshot);
        Assert.Equal(1, result.Snapshot.BacktestRunId);
        Assert.Equal(request.CurrentDate.Date, result.Snapshot.Date);

        var saved = await _snapshotRepository.GetByRunIdAsync(1);
        Assert.Single(saved);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotIncludeFutureData()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 2, 1);
        var simulatedDate = startDate.AddDays(4);

        // 10 candles total; only the first 5 (up to simulatedDate) must be used.
        provider.SetData("TSLA", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 2,
            CurrentDate = simulatedDate,
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["TSLA"]
        };

        var result = await processor.ProcessAsync(request);

        // The provider must have been queried with to=simulatedDate (no look-ahead).
        Assert.Equal(simulatedDate.Date, provider.LastRangeTo);
        Assert.Equal(simulatedDate.Date, result.Snapshot.Date);
    }

    [Fact]
    public async Task ProcessAsync_ToleratesMissingSymbolData()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 3, 1);

        // AAPL has data; MISSING has none.
        provider.SetData("AAPL", BuildCandles(startDate, 10, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 3,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MISSING"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(3, result.Snapshot.BacktestRunId);
    }

    [Fact]
    public async Task ProcessAsync_BuildsAnalyzerFromEachRequestConfiguration()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 3, 1);
        var candles = BuildCandles(startDate, 10);

        foreach (var candle in candles)
        {
            candle.High = candle.Close + 3m;
            candle.Low = candle.Close - 3m;
        }

        provider.SetData("AAPL", candles);
        var processor = BuildProcessor(provider);

        var withoutFilter = await processor.ProcessAsync(
            new BacktestDayRequest
            {
                BacktestRunId = 12,
                CurrentDate = startDate.AddDays(9),
                HistoricalStartDate = startDate,
                StrategyConfiguration = new StrategyConfiguration
                {
                    Id = "without-filter",
                    UseQualityFilter = false
                },
                Portfolio = new Portfolio
                {
                    Name = "Without filter",
                    StartingCash = 10_000m,
                    Cash = 10_000m
                },
                Symbols = ["AAPL"]
            });

        var withFilter = await processor.ProcessAsync(
            new BacktestDayRequest
            {
                BacktestRunId = 13,
                CurrentDate = startDate.AddDays(9),
                HistoricalStartDate = startDate,
                StrategyConfiguration = new StrategyConfiguration
                {
                    Id = "with-filter",
                    UseQualityFilter = true
                },
                Portfolio = new Portfolio
                {
                    Name = "With filter",
                    StartingCash = 10_000m,
                    Cash = 10_000m
                },
                Symbols = ["AAPL"]
            });

        Assert.Single(withoutFilter.Recommendations);
        Assert.Empty(withFilter.Recommendations);
    }

    [Fact]
    public async Task ProcessAsync_ExtendsHistoryWithoutChangingStrategyLookbackScore()
    {
        var provider = new FakeMarketDataProvider();
        var currentDate = new DateTime(2025, 1, 31);
        var candles =
            BuildCandles(
                currentDate.AddDays(
                    -Week52HighCalculator.DefaultTradingSessions + 1),
                Week52HighCalculator.DefaultTradingSessions);
        candles[0].High = 1_000m;
        provider.SetData("AAPL", candles);
        var processor = BuildProcessor(provider);
        var strategy = new StrategyConfiguration
        {
            Id = "short-lookback",
            LookbackDays = 60,
            UseRsi = true
        };
        var historicalStartDate =
            currentDate.AddDays(-strategy.LookbackDays);
        var expected =
            new MarketObservationAnalyzer(strategy)
                .Analyze(
                    "AAPL",
                    candles
                        .Where(x =>
                            x.Date.Date >= historicalStartDate.Date)
                        .ToList());

        var result =
            await processor.ProcessAsync(
                new BacktestDayRequest
                {
                    BacktestRunId = 14,
                    CurrentDate = currentDate,
                    HistoricalStartDate = historicalStartDate,
                    StrategyConfiguration = strategy,
                    Portfolio = new Portfolio
                    {
                        Name = "Short lookback",
                        StartingCash = 10_000m,
                        Cash = 10_000m
                    },
                    Symbols = ["AAPL"]
                });

        var recommendation = Assert.Single(result.Recommendations);
        Assert.Equal(expected.Score, recommendation.Score);
        Assert.Equal(
            MarketHistoryRequirements.GetEffectiveStartDate(
                historicalStartDate,
                currentDate),
            provider.LastRangeFrom);
        Assert.Equal(currentDate.Date, provider.LastRangeTo);
    }

    [Fact]
    public async Task ProcessAsync_StillCompletesWhenNoRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 4, 1);

        // Only 3 candles — fewer than the minimum of 5, so no analysis or recommendations.
        provider.SetData("AAPL", BuildCandles(startDate, 3, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 4,
            CurrentDate = startDate.AddDays(2),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Empty(result.Recommendations);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(10_000m, result.Portfolio.Cash);
    }

    [Fact]
    public async Task ProcessAsync_ReturnsUpdatedPortfolioAndSnapshot()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 5, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 150m));

        var processor = BuildProcessor(provider);

        var currentDate = startDate.AddDays(9);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 5,
            CurrentDate = currentDate,
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.NotNull(result.Portfolio);
        Assert.Equal(currentDate.Date, result.CurrentDate);
        Assert.Equal(currentDate.Date, result.Snapshot.Date);
        Assert.Equal(5, result.Snapshot.BacktestRunId);
        Assert.True(result.Snapshot.PortfolioValue > 0m);
    }

    [Fact]
    public async Task ProcessAsync_SetsZeroTradesExecutedAndFalseRebalanceExecuted_WhenNoRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 6, 1);

        // Only 3 candles — fewer than the minimum of 5, so no recommendations or trades.
        provider.SetData("AAPL", BuildCandles(startDate, 3, 150m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 6,
            CurrentDate = startDate.AddDays(2),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Equal(0, result.Snapshot.TradesExecuted);
        Assert.False(result.Snapshot.RebalanceExecuted);
    }

    [Fact]
    public async Task ProcessAsync_SetsRebalanceExecutedTrue_WhenRecommendationsExist()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 7, 1);

        // Enough candles to produce recommendations and trigger rebalancing.
        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 7,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        // Recommendations were available, so rebalance must have been executed.
        Assert.True(result.Snapshot.RebalanceExecuted);
    }

    [Fact]
    public async Task ProcessAsync_TradesExecutedMatchesPortfolioTradesAdded()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 8, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));

        var processor = BuildProcessor(provider);

        var portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m };

        var request = new BacktestDayRequest
        {
            BacktestRunId = 8,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = portfolio,
            Symbols = ["AAPL", "MSFT"]
        };

        var result = await processor.ProcessAsync(request);

        // The snapshot must record exactly as many trades as were added to the portfolio.
        Assert.Equal(result.Portfolio.Trades.Count, result.Snapshot.TradesExecuted);
    }

    [Fact]
    public async Task ProcessAsync_RespectsMaxPositionsSetting_WhenBuildingRecommendations()
    {
        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 9, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 100m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 300m));

        var processor = BuildProcessor(provider, maxPositions: 1);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 9,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        Assert.Single(result.Recommendations);
    }

    [Fact]
    public async Task ProcessAsync_EnforcesMaxPositionWeight_InBacktestFinalAllocationFlow()
    {
        const decimal weightTolerance = 0.01m;

        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 10, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 10m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 250m));

        const decimal maxPositionWeight = 25m;

        var processor = BuildProcessor(
            provider,
            maxPositionWeight: maxPositionWeight);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 10,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        var totalValue =
            result.Portfolio.Cash +
            result.Portfolio.Positions.Sum(
                x => x.Quantity * x.CurrentPrice);

        Assert.All(
            result.Portfolio.Positions,
            position =>
            {
                var weight =
                    position.Quantity *
                    position.CurrentPrice /
                    totalValue * 100m;

                Assert.True(weight <= maxPositionWeight + weightTolerance);
            });

        Assert.True(result.Portfolio.Cash > 0m);
    }

    [Fact]
    public async Task ProcessAsync_UncappedFlowStillAllowsNaturalHigherWeight()
    {
        const decimal cappedThreshold = 25m;

        var provider = new FakeMarketDataProvider();
        var startDate = new DateTime(2024, 11, 1);

        provider.SetData("AAPL", BuildCandles(startDate, 10, 10m));
        provider.SetData("MSFT", BuildCandles(startDate, 10, 200m));
        provider.SetData("GOOG", BuildCandles(startDate, 10, 250m));

        var processor = BuildProcessor(provider, maxPositionWeight: 100m);

        var request = new BacktestDayRequest
        {
            BacktestRunId = 11,
            CurrentDate = startDate.AddDays(9),
            HistoricalStartDate = startDate,
            Portfolio = new Portfolio { Name = "Backtest", StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "GOOG"]
        };

        var result = await processor.ProcessAsync(request);

        var totalValue =
            result.Portfolio.Cash +
            result.Portfolio.Positions.Sum(
                x => x.Quantity * x.CurrentPrice);

        var highestWeight =
            result.Portfolio.Positions
                .Select(
                    x => x.Quantity * x.CurrentPrice / totalValue * 100m)
                .DefaultIfEmpty(0m)
                .Max();

        Assert.True(highestWeight > cappedThreshold);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessAsync_ExposesRelativeStrength_UsingSelectedBenchmarkAndSimulatedDate(bool enabled)
    {
        var start = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider();
        provider.SetData("AAPL", BuildCandles(start, 10, 100m));
        provider.SetData("MSFT", BuildCandles(start, 10, 100m));
        provider.SetData("QQQ", BuildCandles(start, 10, 200m));
        var result = await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = start.AddDays(4),
            HistoricalStartDate = start,
            StrategyConfiguration = new StrategyConfiguration
            {
                UseRelativeStrength = enabled, RelativeStrengthBenchmarkSymbol = "QQQ",
                RelativeStrengthScoreWeight = 3m
            },
            Portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL", "MSFT", "MISSING"]
        });

        Assert.Equal(enabled ? 1 : 0, provider.Calls.Count(x => x == "QQQ"));
        Assert.Equal(2, result.Observations.Count);
        Assert.All(result.Observations, observation =>
        {
            Assert.Equal(enabled ? 2m : (decimal?)null, observation.RelativeStrengthPercent);
            Assert.Equal(enabled ? "QQQ" : null, observation.RelativeStrengthBenchmarkSymbol);
            Assert.Equal(4m, observation.MomentumPercent);
            Assert.Equal(enabled ? 6m : 0m, observation.RelativeStrengthScoreContribution);
            var baseline = new MarketObservationAnalyzer()
                .Analyze(observation.Symbol, BuildCandles(start, 5, 100m));
            Assert.Equal(baseline.Score + (enabled ? 6m : 0m), observation.Score);
        });
        Assert.Equal(start.AddDays(4), provider.LastRangeTo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessAsync_MissingOrFailedBenchmarkDoesNotPreventStockAnalysis(bool fail)
    {
        var start = new DateTime(2024, 1, 1);
        var provider = new FakeMarketDataProvider { ThrowForSymbol = fail ? "SPY" : null };
        provider.SetData("AAPL", BuildCandles(start, 5, 100m));

        var result = await BuildProcessor(provider).ProcessAsync(new BacktestDayRequest
        {
            CurrentDate = start.AddDays(4), HistoricalStartDate = start,
            Portfolio = new Portfolio { StartingCash = 10_000m, Cash = 10_000m },
            Symbols = ["AAPL"]
        });

        Assert.Null(Assert.Single(result.Observations).RelativeStrengthPercent);
        Assert.Single(result.Recommendations);
    }

    private sealed class CountingBacktestRepositories(TradingSimulatorDbContext dbContext)
        : IBacktestRunRepository, IBacktestSnapshotRepository, IFeatureSnapshotRepository
    {
        private readonly BacktestRunRepository _runs = new(dbContext);
        private readonly BacktestSnapshotRepository _snapshots = new(dbContext);
        private readonly FeatureSnapshotRepository _features = new(dbContext);
        public List<string> Calls { get; } = [];

        public Task AddAsync(BacktestRun run)
        {
            Calls.Add("AddRun");
            return _runs.AddAsync(run);
        }

        public Task UpdateAsync(BacktestRun run)
        {
            Calls.Add("UpdateRun");
            return _runs.UpdateAsync(run);
        }

        public Task MarkFailedAsync(int runId)
        {
            Calls.Add("MarkFailed");
            return _runs.MarkFailedAsync(runId);
        }

        public Task<bool> ExistsAsync(int id)
        {
            Calls.Add("Exists");
            return _runs.ExistsAsync(id);
        }

        public Task<BacktestRun?> GetByIdAsync(int id)
        {
            Calls.Add("GetRun");
            return _runs.GetByIdAsync(id);
        }

        public Task AddAsync(BacktestSnapshot snapshot)
        {
            Calls.Add("AddSnapshot");
            return _snapshots.AddAsync(snapshot);
        }

        public Task AddRangeAsync(IReadOnlyList<BacktestSnapshot> snapshots)
        {
            Calls.Add("AddSnapshots");
            return _snapshots.AddRangeAsync(snapshots);
        }

        public Task<List<BacktestSnapshot>> GetByRunIdAsync(int runId)
        {
            Calls.Add("GetSnapshots");
            return _snapshots.GetByRunIdAsync(runId);
        }

        public Task SaveAsync(IReadOnlyList<ObservationResult> observations,
            StrategyConfiguration strategyConfiguration, int? backtestRunId = null)
        {
            Calls.Add("SaveFeatures");
            return _features.SaveAsync(observations, strategyConfiguration, backtestRunId);
        }

        public Task<List<FeatureSnapshot>> QueryAsync(DateOnly from, DateOnly to,
            string? symbol = null, string? strategyId = null, int? backtestRunId = null)
        {
            Calls.Add("QueryFeatures");
            return _features.QueryAsync(from, to, symbol, strategyId, backtestRunId);
        }
    }

    private sealed class FakeMarketDataProvider : IMarketDataProvider
    {
        private readonly Dictionary<string, IReadOnlyList<MarketCandle>> _data
            = new(StringComparer.OrdinalIgnoreCase);

        public DateTime LastRangeTo { get; private set; }

        public DateTime LastRangeFrom { get; private set; }

        public List<string> Calls { get; } = [];

        public string? ThrowForSymbol { get; init; }
        public Action<string>? OnFetch { get; init; }

        public void SetData(string symbol, IReadOnlyList<MarketCandle> candles)
        {
            _data[symbol] = candles;
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
        {
            return GetHistoricalDailyAsync(symbol, from, DateTime.MaxValue);
        }

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
        {
            LastRangeFrom = from.Date;
            LastRangeTo = to.Date;
            Calls.Add(symbol);
            OnFetch?.Invoke(symbol);

            if (symbol == ThrowForSymbol)
            {
                throw new InvalidOperationException("Benchmark unavailable.");
            }

            if (!_data.TryGetValue(symbol, out var candles))
            {
                return Task.FromResult<IReadOnlyList<MarketCandle>>([]);
            }

            var result = candles
                .Where(x => x.Date.Date >= from.Date && x.Date.Date <= to.Date)
                .OrderBy(x => x.Date)
                .ToList();

            return Task.FromResult<IReadOnlyList<MarketCandle>>(result);
        }
    }
}
