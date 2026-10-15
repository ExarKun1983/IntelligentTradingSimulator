using System;
using System.Collections.Generic;
using System.Linq;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ProgressIdentifiesVariantsAndReportsFailureBeforeContinuing(bool throws)
    {
        var progress = new SimulationProgressRecorder
        {
            Context = new WorkflowProgressContext { RunId = 999, CompletedDates = 99 }
        };
        var engine = new ProgressCapturingEngine(progress, throws);
        var runner = new StrategyVariantRunner(engine, progress);
        var date = new DateTime(2024, 1, 1);

        var result = await runner.RunAsync(
            [BuildVariant("fail"), BuildVariant("pass")], BuildRunRequest(date, date));

        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, engine.Contexts.Count);
        for (var index = 0; index < engine.Contexts.Count; index++)
        {
            var context = engine.Contexts[index];
            Assert.Equal(index == 0 ? "fail" : "pass", context.StrategyId);
            Assert.Equal(index + 1, context.VariantIndex);
            Assert.Equal(2, context.TotalVariants);
            Assert.Null(context.RunId);
            Assert.Null(context.CompletedDates);
            Assert.Null(context.Date);
        }
        var terminals = progress.Reports.Where(x => x.Force && x.Message.StartsWith("Strategy variant")).ToList();
        Assert.Equal(new[] { "Strategy variant failed", "Strategy variant complete" },
            terminals.Select(x => x.Message));
        Assert.Equal("fail", terminals[0].Context.StrategyId);
        Assert.Equal("pass", terminals[1].Context.StrategyId);
        Assert.Equal(42, terminals[1].Context.RunId);
        Assert.Equal(0, progress.ActiveOperations);
    }

    [Fact]
    public async Task RunAsync_ConsoleProgressAnnouncesEveryQuickVariantBeforeEngineCall()
    {
        using var output = new System.IO.StringWriter();
        using var progress = new ConsoleWorkflowProgress(output, interval: TimeSpan.FromDays(1));
        var engine = new CapturingEngine
        {
            OnRun = request => Assert.Contains(
                $"Starting strategy variant | Workflow: backtest | Strategy: {request.StrategyId}",
                output.ToString())
        };
        var date = new DateTime(2024, 1, 1);

        await new StrategyVariantRunner(engine, progress).RunAsync(
            [BuildVariant("alpha"), BuildVariant("beta")], BuildRunRequest(date, date));

        var starts = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.StartsWith("Starting strategy variant")).ToList();
        Assert.Equal(2, starts.Count);
        Assert.Contains("Strategy: alpha", starts[0]);
        Assert.Contains("Variant: 1/2", starts[0]);
        Assert.Contains("Strategy: beta", starts[1]);
        Assert.Contains("Variant: 2/2", starts[1]);
        Assert.Equal(2, engine.Requests.Count);
    }

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
    public async Task RunAsync_EachRequestCarriesItsVariantConfiguration()
    {
        var engine = new CapturingEngine();
        var runner = new StrategyVariantRunner(engine);
        var variants = new List<StrategyConfiguration>
        {
            new()
            {
                Id = "without-rsi",
                UseRsi = false
            },
            new()
            {
                Id = "with-rsi",
                UseRsi = true
            }
        };

        await runner.RunAsync(
            variants,
            BuildRunRequest(
                new DateTime(2024, 4, 1),
                new DateTime(2024, 4, 1)));

        Assert.Equal(2, engine.Requests.Count);
        Assert.Same(variants[0], engine.Requests[0].StrategyConfiguration);
        Assert.False(engine.Requests[0].StrategyConfiguration.UseRsi);
        Assert.Same(variants[1], engine.Requests[1].StrategyConfiguration);
        Assert.True(engine.Requests[1].StrategyConfiguration.UseRsi);
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

    [Fact]
    public async Task RunAsync_WhenOneVariantThrows_RemainingVariantsStillRun()
    {
        var throwingEngine = new ThrowingEngine(throwOnStrategyId: "bad-variant");
        var runner = new StrategyVariantRunner(throwingEngine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("good-v1"),
            BuildVariant("bad-variant"),
            BuildVariant("good-v2")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 8, 1),
            endDate: new DateTime(2024, 8, 1));

        var batchResult = await runner.RunAsync(variants, request);

        Assert.Equal(3, batchResult.Results.Count);
        Assert.Equal(BacktestRunStatus.Completed, batchResult.Results[0].EngineResult.Status);
        Assert.Equal(BacktestRunStatus.Failed, batchResult.Results[1].EngineResult.Status);
        Assert.Equal(BacktestRunStatus.Completed, batchResult.Results[2].EngineResult.Status);
    }

    [Fact]
    public async Task RunAsync_WhenOneVariantThrows_FailedResultContainsErrorMessage()
    {
        var throwingEngine = new ThrowingEngine(throwOnStrategyId: "exploding-variant");
        var runner = new StrategyVariantRunner(throwingEngine);

        var variants = new List<StrategyConfiguration>
        {
            BuildVariant("exploding-variant"),
            BuildVariant("safe-variant")
        };

        var request = BuildRunRequest(
            startDate: new DateTime(2024, 9, 1),
            endDate: new DateTime(2024, 9, 1));

        var batchResult = await runner.RunAsync(variants, request);

        Assert.Equal(2, batchResult.Results.Count);
        Assert.Equal(BacktestRunStatus.Failed, batchResult.Results[0].EngineResult.Status);
        Assert.NotNull(batchResult.Results[0].EngineResult.ErrorMessage);
        Assert.Equal(BacktestRunStatus.Completed, batchResult.Results[1].EngineResult.Status);
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

    /// <summary>
    /// Stub engine that throws <see cref="InvalidOperationException"/> for a
    /// nominated strategy ID and succeeds (returning a completed result) for
    /// all others. Used to verify that the runner isolates per-variant failures.
    /// </summary>
    private sealed class ThrowingEngine : IBacktestEngine
    {
        private readonly string _throwOnStrategyId;
        private int _runId;

        public ThrowingEngine(string throwOnStrategyId)
            => _throwOnStrategyId = throwOnStrategyId;

        public Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
        {
            if (string.Equals(request.StrategyId, _throwOnStrategyId, StringComparison.Ordinal))
                throw new InvalidOperationException($"Simulated engine failure for strategy '{request.StrategyId}'.");

            return Task.FromResult(new BacktestEngineResult
            {
                RunId = ++_runId,
                Status = BacktestRunStatus.Completed,
                FinalCapital = request.InitialCapital,
                TotalProfit = 0m,
                TotalReturnPercent = 0m
            });
        }
    }

    private sealed class CapturingEngine : IBacktestEngine
    {
        public List<BacktestEngineRequest> Requests { get; } = [];
        public Action<BacktestEngineRequest>? OnRun { get; init; }

        public Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
        {
            OnRun?.Invoke(request);
            Requests.Add(request);

            return Task.FromResult(new BacktestEngineResult
            {
                RunId = Requests.Count,
                Status = BacktestRunStatus.Completed
            });
        }
    }

    private sealed class ProgressCapturingEngine(IWorkflowProgress progress, bool throws) : IBacktestEngine
    {
        public List<WorkflowProgressContext> Contexts { get; } = [];

        public Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
        {
            Contexts.Add(progress.Context);
            if (Contexts.Count == 1 && throws)
                throw new InvalidOperationException("Variant failed");

            return Task.FromResult(new BacktestEngineResult
            {
                RunId = 42,
                Status = Contexts.Count == 1 ? BacktestRunStatus.Failed : BacktestRunStatus.Completed
            });
        }
    }
}
