using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class StrategyVariantRunnerTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;

    public StrategyVariantRunnerTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
    }

    public void Dispose() => _dbContext.Dispose();

    private IBacktestEngine BuildEngine()
        => new BacktestEngine(_runRepository, new StubDayProcessor());

    private static StrategyVariantRunRequest BuildRunRequest(
        DateTime startDate,
        DateTime endDate)
        => new StrategyVariantRunRequest
        {
            NamePrefix = "Test Variant Run",
            StartDate = startDate,
            EndDate = endDate,
            HistoricalStartDate = startDate.AddDays(-30),
            InitialCapital = 10_000m,
            Symbols = ["AAPL", "MSFT"]
        };

    private static StrategyConfiguration BuildVariant(string id, int maxPositions = 5)
        => new StrategyConfiguration
        {
            Id = id,
            MaxPositions = maxPositions,
            MaxPositionWeight = 25m,
            LookbackDays = 90
        };

    [Fact]
    public async Task RunAsync_ReturnsOneResultPerVariant()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("alpha"),
            BuildVariant("beta"),
            BuildVariant("gamma")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 1, 1),
            endDate: new DateTime(2024, 1, 2));

        var batchResult = await runner.RunAsync(variants, request);

        Assert.Equal(3, batchResult.Results.Count);
    }

    [Fact]
    public async Task RunAsync_EachResultHasDistinctStrategyId()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("momentum-v1"),
            BuildVariant("momentum-v2"),
            BuildVariant("momentum-v3")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 2, 1),
            endDate: new DateTime(2024, 2, 1));

        var batchResult = await runner.RunAsync(variants, request);

        var ids = new HashSet<string>();
        foreach (var result in batchResult.Results)
        {
            Assert.True(ids.Add(result.StrategyId),
                $"Duplicate strategy ID found: {result.StrategyId}");
        }
    }

    [Fact]
    public async Task RunAsync_EachVariantRunReceivesDistinctBacktestRunId()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("alpha"),
            BuildVariant("beta")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 3, 1),
            endDate: new DateTime(2024, 3, 1));

        var batchResult = await runner.RunAsync(variants, request);

        var runIds = new HashSet<int>();
        foreach (var result in batchResult.Results)
        {
            Assert.True(runIds.Add(result.EngineResult.RunId),
                $"Duplicate RunId found: {result.EngineResult.RunId}");
        }
    }

    [Fact]
    public async Task RunAsync_StrategyIdMatchesVariantDefinition()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("risk-capped-v1"),
            BuildVariant("risk-capped-v2")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 4, 1),
            endDate: new DateTime(2024, 4, 1));

        var batchResult = await runner.RunAsync(variants, request);

        Assert.Equal(2, batchResult.Results.Count);
        Assert.Equal("risk-capped-v1", batchResult.Results[0].StrategyId);
        Assert.Equal("risk-capped-v2", batchResult.Results[1].StrategyId);
    }

    [Fact]
    public async Task RunAsync_PersistedBacktestRunsHaveCorrectStrategyIds()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("conservative"),
            BuildVariant("aggressive")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 5, 1),
            endDate: new DateTime(2024, 5, 1));

        var batchResult = await runner.RunAsync(variants, request);

        foreach (var variantResult in batchResult.Results)
        {
            var persisted = await _runRepository.GetByIdAsync(variantResult.EngineResult.RunId);
            Assert.NotNull(persisted);
            Assert.Equal(variantResult.StrategyId, persisted.StrategyId);
        }
    }

    [Fact]
    public async Task RunAsync_AllVariantsCompleteSuccessfully()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("v1"),
            BuildVariant("v2")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 6, 1),
            endDate: new DateTime(2024, 6, 3));

        var batchResult = await runner.RunAsync(variants, request);

        foreach (var result in batchResult.Results)
        {
            Assert.Equal(BacktestRunStatus.Completed, result.EngineResult.Status);
        }
    }

    [Fact]
    public async Task RunAsync_WithSingleVariant_ReturnsSingleResult()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("solo")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 7, 1),
            endDate: new DateTime(2024, 7, 1));

        var batchResult = await runner.RunAsync(variants, request);

        Assert.Single(batchResult.Results);
        Assert.Equal("solo", batchResult.Results[0].StrategyId);
    }

    [Fact]
    public void Constructor_Throws_WhenEngineIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => new StrategyVariantRunner(null!));
    }

    [Fact]
    public async Task RunAsync_Throws_WhenVariantsIsNull()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);
        var request = BuildRunRequest(new DateTime(2024, 1, 1), new DateTime(2024, 1, 1));

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.RunAsync(null!, request));
    }

    [Fact]
    public async Task RunAsync_Throws_WhenRequestIsNull()
    {
        var engine = BuildEngine();
        var runner = new StrategyVariantRunner(engine);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => runner.RunAsync([BuildVariant("x")], null!));
    }

    // ── Stubs ──────────────────────────────────────────────────────────────

    private sealed class StubDayProcessor : IBacktestDayProcessor
    {
        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            return Task.FromResult(new BacktestDayResult
            {
                CurrentDate = request.CurrentDate.Date,
                Portfolio = request.Portfolio,
                Snapshot = new BacktestSnapshot
                {
                    BacktestRunId = request.BacktestRunId,
                    Date = request.CurrentDate.Date,
                    PortfolioValue = request.Portfolio.TotalValue,
                    Cash = request.Portfolio.Cash
                },
                Recommendations = []
            });
        }
    }
}
