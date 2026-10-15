using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestEngineTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;

    public BacktestEngineTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
    }

    public void Dispose() => _dbContext.Dispose();

    private BacktestEngine BuildEngine(IBacktestDayProcessor dayProcessor)
        => new BacktestEngine(_runRepository, dayProcessor);

    private IBacktestEngine BuildEngineAsInterface(IBacktestDayProcessor dayProcessor)
        => new BacktestEngine(_runRepository, dayProcessor);

    private static BacktestEngineRequest BuildRequest(
        DateTime startDate,
        DateTime endDate,
        decimal initialCapital = 10_000m)
        => new BacktestEngineRequest
        {
            Name = "Test Run",
            StrategyId = "momentum-default",
            StrategyConfiguration = new StrategyConfiguration
            {
                Id = "momentum-default"
            },
            StartDate = startDate,
            EndDate = endDate,
            HistoricalStartDate = startDate.AddDays(-30),
            InitialCapital = initialCapital,
            Symbols = ["AAPL", "MSFT"]
        };

    private static StrategyConfiguration LoadSelectedStrategyConfiguration(
        string strategyId)
    {
        var configuration =
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["ActiveStrategy"] = strategyId,
                        [$"Strategies:{strategyId}:MaxPositions"] = "8",
                        [$"Strategies:{strategyId}:MaxPositionWeight"] = "25"
                    })
                .Build();

        return StrategyConfigurationLoader.Load(configuration);
    }

    [Fact]
    public async Task RunAsync_CreatesRunAndCompletesOverDateRange()
    {
        var startDate = new DateTime(2024, 1, 1);
        var endDate = new DateTime(2024, 1, 3);

        var processor = new StubDayProcessor();
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate));

        Assert.True(result.RunId > 0);
        Assert.Equal(BacktestRunStatus.Completed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal("Test Run", saved.Name);
        Assert.Equal("momentum-default", saved.StrategyId);
        Assert.Equal(startDate.Date, saved.StartDate);
        Assert.Equal(endDate.Date, saved.EndDate);
    }

    [Fact]
    public async Task RunAsync_PersistsStrategyIdentityInBacktestRun()
    {
        var startDate = new DateTime(2024, 1, 1);
        var endDate = new DateTime(2024, 1, 1);
        var selectedStrategyConfiguration =
            LoadSelectedStrategyConfiguration("risk-capped-v1");
        var requestFactory =
            new BacktestEngineRequestFactory(selectedStrategyConfiguration);

        var processor = new StubDayProcessor();
        var engine = BuildEngine(processor);
        var request =
            requestFactory.Create(
                name: "Test Run",
                startDate: startDate,
                endDate: endDate,
                historicalStartDate: startDate.AddDays(-30),
                initialCapital: 10_000m,
                symbols: ["AAPL", "MSFT"]);

        var result = await engine.RunAsync(request);

        var saved = await _runRepository.GetByIdAsync(result.RunId);

        Assert.NotNull(saved);
        Assert.Equal(selectedStrategyConfiguration.Id, saved.StrategyId);
        Assert.Same(
            selectedStrategyConfiguration,
            request.StrategyConfiguration);
    }

    [Fact]
    public async Task RunAsync_PassesStrategyConfigurationToEachDayRequest()
    {
        var startDate = new DateTime(2024, 1, 1);
        var configuration = new StrategyConfiguration
        {
            Id = "request-specific",
            UseRsi = true
        };
        var request = BuildRequest(startDate, startDate.AddDays(1));
        request.StrategyId = configuration.Id;
        request.StrategyConfiguration = configuration;
        var processor = new StubDayProcessor();
        var engine = BuildEngine(processor);

        await engine.RunAsync(request);

        Assert.Equal(2, processor.CallCount);
        Assert.Same(configuration, processor.LastStrategyConfiguration);
    }

    [Fact]
    public async Task RunAsync_CallsDayProcessorForEachDayInRange()
    {
        var startDate = new DateTime(2024, 2, 1);
        var endDate = new DateTime(2024, 2, 5);

        var processor = new StubDayProcessor();
        var engine = BuildEngine(processor);

        await engine.RunAsync(BuildRequest(startDate, endDate));

        // 5 days: Feb 1 through Feb 5 inclusive
        Assert.Equal(5, processor.CallCount);
        Assert.Equal(startDate.Date, processor.FirstRequestDate);
        Assert.Equal(endDate.Date, processor.LastRequestDate);
    }

    [Fact]
    public async Task RunAsync_CarriesPortfolioStateFromOneDayToNext()
    {
        var startDate = new DateTime(2024, 3, 1);
        var endDate = new DateTime(2024, 3, 3);

        var processor = new PortfolioMutatingDayProcessor(cashDelta: 500m);
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate, initialCapital: 10_000m));

        // Each day adds 500 cash; 3 days = 1500 total added
        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        Assert.Equal(11_500m, result.FinalCapital);
    }

    [Fact]
    public async Task RunAsync_MarksRunCompletedOnSuccess()
    {
        var startDate = new DateTime(2024, 4, 1);
        var endDate = new DateTime(2024, 4, 2);

        var processor = new StubDayProcessor();
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate));

        Assert.Equal(BacktestRunStatus.Completed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Completed, saved.Status);
    }

    [Fact]
    public async Task RunAsync_CompletionProgressIoFailureDoesNotMarkPersistedRunFailed()
    {
        var engine = new BacktestEngine(
            _runRepository,
            new StubDayProcessor(),
            new CompletionReportFailingProgress());

        var result = await engine.RunAsync(
            BuildRequest(new DateTime(2024, 4, 1), new DateTime(2024, 4, 1)));

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        Assert.Equal(10_000m, result.FinalCapital);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Completed, saved.Status);
        Assert.Equal(result.FinalCapital, saved.FinalCapital);
    }

    [Fact]
    public async Task RunAsync_MarksRunFailedWhenDayProcessorThrows()
    {
        var startDate = new DateTime(2024, 5, 1);
        var endDate = new DateTime(2024, 5, 3);

        var processor = new ThrowingDayProcessor(throwOnDay: startDate.AddDays(1));
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate));

        Assert.Equal(BacktestRunStatus.Failed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Failed, saved.Status);
    }

    [Fact]
    public async Task RunAsync_MarksRunFailedWhenFinalPortfolioSaveThrows()
    {
        var repository = new FinalSaveFailingRepository(_dbContext);
        var progress = new SimulationProgressRecorder();
        var engine = new BacktestEngine(repository, new StubDayProcessor(), progress);

        var result = await engine.RunAsync(
            BuildRequest(new DateTime(2024, 5, 1), new DateTime(2024, 5, 1)));

        Assert.Equal(BacktestRunStatus.Failed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Failed, saved.Status);
        Assert.Null(saved.Portfolio);
        Assert.Empty(await _dbContext.Portfolios.ToListAsync());
        Assert.Equal(0, progress.ActiveOperations);
        Assert.Equal(
            new[] { "Creating backtest run", "Processing simulated dates", "Saving final backtest result", "Marking backtest failed" },
            progress.Operations.Select(x => x.Operation));
        Assert.Equal("Simulation failed", progress.Reports.Last().Message);
        Assert.True(progress.Reports.Last().Force);
    }

    [Fact]
    public async Task RunAsync_AnnouncesPersistedIdAndKeepsDateScopeWhileProcessorIsPending()
    {
        var date = new DateTime(2024, 1, 5);
        var progress = new SimulationProgressRecorder();
        var processor = new PendingDayProcessor();
        var pendingRun = new BacktestEngine(_runRepository, processor, progress)
            .RunAsync(BuildRequest(date, date));
        var request = await processor.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            Assert.False(pendingRun.IsCompleted);
            Assert.Equal(1, progress.ActiveOperations);
            var operation = progress.Operations.Last();
            Assert.Equal("Processing simulated dates", operation.Operation);
            Assert.Equal(request.BacktestRunId, operation.Context.RunId);
            Assert.True(operation.Context.RunId > 0);
            Assert.Equal(date, operation.Context.Date);
            Assert.Equal(0, operation.Context.CompletedDates);
            Assert.Equal(1, operation.Context.TotalDates);
            var created = Assert.Single(progress.Reports, x => x.Message == "Backtest run created");
            Assert.True(created.Force);
            Assert.Equal(request.BacktestRunId, created.Context.RunId);
        }
        finally
        {
            processor.Completion.SetResult(new BacktestDayResult
            {
                Portfolio = request.Portfolio,
                Snapshot = new BacktestSnapshot { PortfolioValue = request.Portfolio.TotalValue }
            });
            await pendingRun;
        }
        Assert.Equal(0, progress.ActiveOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_ProgressCountsSuccessfulCalendarDatesAndClearsStaleContext(bool fail)
    {
        var start = new DateTime(2024, 1, 5);
        var progress = new SimulationProgressRecorder
        {
            Context = new WorkflowProgressContext
            {
                Workflow = "stale", StrategyId = "stale", RunId = 999, Date = start.AddYears(-1),
                CompletedDates = 99, TotalDates = 99, CompletedSymbols = 99, TotalSymbols = 99,
                VariantIndex = 2, TotalVariants = 3
            }
        };
        var processor = new StubDayProcessor();
        var engine = new BacktestEngine(_runRepository,
            fail ? new ThrowingDayProcessor(start.AddDays(1)) : processor, progress);

        var result = await engine.RunAsync(BuildRequest(start, start.AddDays(2)));

        var creating = progress.Operations.First();
        Assert.Equal("Creating backtest run", creating.Operation);
        Assert.Equal("backtest", creating.Context.Workflow);
        Assert.Equal("momentum-default", creating.Context.StrategyId);
        Assert.Null(creating.Context.RunId);
        Assert.Null(creating.Context.Date);
        Assert.Null(creating.Context.CompletedSymbols);
        Assert.Null(creating.Context.TotalSymbols);
        Assert.Equal(0, creating.Context.CompletedDates);
        Assert.Equal(3, creating.Context.TotalDates);
        var days = progress.Reports.Where(x => x.Message == "Processing simulation date").ToList();
        Assert.Equal(fail ? 2 : 3, days.Count);
        for (var index = 0; index < days.Count; index++)
        {
            Assert.Equal(start.AddDays(index), days[index].Context.Date);
            Assert.Equal(index, days[index].Context.CompletedDates);
            Assert.Equal(result.RunId, days[index].Context.RunId);
        }
        Assert.Equal(fail ? 1 : 3, progress.Context.CompletedDates);
        Assert.Equal(2, progress.Context.VariantIndex);
        Assert.Equal(3, progress.Context.TotalVariants);
        Assert.Equal(fail ? "Simulation failed" : "Simulation complete", progress.Reports.Last().Message);
        Assert.True(progress.Reports.Last().Force);
        Assert.DoesNotContain(progress.Reports, x => x.Message == "Workflow complete");
        Assert.Equal(0, progress.ActiveOperations);
        if (!fail)
            Assert.Equal(3, processor.CallCount);
    }

    [Fact]
    public async Task RunAsync_FinalizesMetricsFromLastDaySnapshot_NotPortfolioTotalValue()
    {
        var startDate = new DateTime(2024, 6, 1);
        var endDate = new DateTime(2024, 6, 2);
        const decimal initialCapital = 10_000m;
        const decimal snapshotPortfolioValue = 12_500m;

        // Processor returns a snapshot whose PortfolioValue differs from
        // portfolio.TotalValue to verify the engine uses the snapshot.
        var processor = new SnapshotOverrideDayProcessor(snapshotPortfolioValue);
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate, initialCapital));

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        Assert.Equal(snapshotPortfolioValue, result.FinalCapital);
        Assert.Equal(snapshotPortfolioValue - initialCapital, result.TotalProfit);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(snapshotPortfolioValue, saved.FinalCapital);
        Assert.Equal(snapshotPortfolioValue - initialCapital, saved.TotalProfit);
    }

    [Fact]
    public async Task RunAsync_IsAccessibleViaIBacktestEngineInterface()
    {
        var startDate = new DateTime(2024, 7, 1);
        var endDate = new DateTime(2024, 7, 2);

        var processor = new StubDayProcessor();
        IBacktestEngine engine = BuildEngineAsInterface(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, endDate));

        Assert.True(result.RunId > 0);
        Assert.Equal(BacktestRunStatus.Completed, result.Status);
    }

    // ── Stubs ──────────────────────────────────────────────────────────────

    private sealed class PendingDayProcessor : IBacktestDayProcessor
    {
        public TaskCompletionSource<BacktestDayRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<BacktestDayResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            Started.SetResult(request);
            return Completion.Task;
        }
    }

    private sealed class StubDayProcessor : IBacktestDayProcessor
    {
        public int CallCount { get; private set; }
        public DateTime FirstRequestDate { get; private set; }
        public DateTime LastRequestDate { get; private set; }
        public StrategyConfiguration? LastStrategyConfiguration { get; private set; }

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            if (CallCount == 0)
                FirstRequestDate = request.CurrentDate.Date;

            CallCount++;
            LastRequestDate = request.CurrentDate.Date;
            LastStrategyConfiguration = request.StrategyConfiguration;

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

    private sealed class PortfolioMutatingDayProcessor : IBacktestDayProcessor
    {
        private readonly decimal _cashDelta;

        public PortfolioMutatingDayProcessor(decimal cashDelta)
        {
            _cashDelta = cashDelta;
        }

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            var portfolio = request.Portfolio;
            portfolio.Cash += _cashDelta;

            return Task.FromResult(new BacktestDayResult
            {
                CurrentDate = request.CurrentDate.Date,
                Portfolio = portfolio,
                Snapshot = new BacktestSnapshot
                {
                    BacktestRunId = request.BacktestRunId,
                    Date = request.CurrentDate.Date,
                    PortfolioValue = portfolio.TotalValue,
                    Cash = portfolio.Cash
                },
                Recommendations = []
            });
        }
    }

    private sealed class ThrowingDayProcessor : IBacktestDayProcessor
    {
        private readonly DateTime _throwOnDay;

        public ThrowingDayProcessor(DateTime throwOnDay)
        {
            _throwOnDay = throwOnDay.Date;
        }

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            if (request.CurrentDate.Date == _throwOnDay)
                throw new InvalidOperationException("Simulated day processing failure.");

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

    private sealed class SnapshotOverrideDayProcessor : IBacktestDayProcessor
    {
        private readonly decimal _snapshotPortfolioValue;

        public SnapshotOverrideDayProcessor(decimal snapshotPortfolioValue)
        {
            _snapshotPortfolioValue = snapshotPortfolioValue;
        }

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
                    PortfolioValue = _snapshotPortfolioValue,
                    Cash = request.Portfolio.Cash
                },
                Recommendations = []
            });
        }
    }

    private sealed class FinalSaveFailingRepository : IBacktestRunRepository
    {
        private readonly TradingSimulatorDbContext _dbContext;
        private readonly BacktestRunRepository _innerRepository;

        public FinalSaveFailingRepository(TradingSimulatorDbContext dbContext)
        {
            _dbContext = dbContext;
            _innerRepository = new BacktestRunRepository(dbContext);
        }

        public Task AddAsync(BacktestRun run) =>
            _innerRepository.AddAsync(run);

        public Task<bool> ExistsAsync(int id) =>
            _innerRepository.ExistsAsync(id);

        public Task<BacktestRun?> GetByIdAsync(int id) =>
            _innerRepository.GetByIdAsync(id);

        public Task UpdateAsync(BacktestRun run)
        {
            _dbContext.BacktestRuns.Update(run);
            throw new InvalidOperationException("Simulated portfolio save failure.");
        }

        public Task MarkFailedAsync(int runId) =>
            _innerRepository.MarkFailedAsync(runId);
    }

    private sealed class CompletionReportFailingProgress : IWorkflowProgress
    {
        public WorkflowProgressContext Context { get; set; } = new();

        public void Report(string message, bool force = false)
        {
            if (message == "Simulation complete")
            {
                throw new IOException("Progress output is unavailable.");
            }
        }

        public IDisposable BeginOperation(string operation, string? symbol = null) =>
            NoopScope.Instance;

        private sealed class NoopScope : IDisposable
        {
            public static NoopScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}

internal sealed class SimulationProgressRecorder : IWorkflowProgress
{
    public WorkflowProgressContext Context { get; set; } = new();
    public List<(string Message, bool Force, WorkflowProgressContext Context)> Reports { get; } = [];
    public List<(string Operation, string? Symbol, WorkflowProgressContext Context)> Operations { get; } = [];
    public int ActiveOperations { get; private set; }

    public void Report(string message, bool force = false) => Reports.Add((message, force, Context));

    public IDisposable BeginOperation(string operation, string? symbol = null)
    {
        Operations.Add((operation, symbol, Context));
        ActiveOperations++;
        return new Scope(this);
    }

    private sealed class Scope(SimulationProgressRecorder owner) : IDisposable
    {
        public void Dispose() => owner.ActiveOperations--;
    }
}
