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
using IntelligentTradingSimulator.Core.Services;
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
    public async Task RunAsync_RejectsUnsupportedOpeningPrecisionBeforePersistenceOrProcessing()
    {
        var date = new DateTime(2024, 1, 1);
        var processor = new StubDayProcessor();
        var request = BuildRequest(date, date, initialCapital: 100.000000005m);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => BuildEngine(processor).RunAsync(request));

        Assert.Equal(0, processor.CallCount);
        Assert.Empty(await _dbContext.BacktestRuns.ToListAsync());
        Assert.Empty(await _dbContext.Portfolios.ToListAsync());
        Assert.Empty(_dbContext.ChangeTracker.Entries());
        Assert.Equal(100.000000005m, request.InitialCapital);
    }

    [Fact]
    public async Task RunAsync_AcceptedOpeningPrecisionPreservesExecutionBaselineOnReload()
    {
        const decimal opening = 100.00000001m;
        var date = new DateTime(2024, 1, 1);
        var result = await BuildEngine(new StubDayProcessor()).RunAsync(BuildRequest(date, date, opening));

        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        _dbContext.ChangeTracker.Clear();
        var portfolio = await _dbContext.Portfolios.SingleAsync();
        Assert.Equal(opening, portfolio.StartingCash);
        Assert.Equal(opening, portfolio.Cash);
        Assert.Empty(portfolio.Trades);
        Assert.Equal(0m, result.TotalProfit);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_RunCreatedProgressOutputFailureDoesNotAbandonRun(bool objectDisposed)
    {
        var progress = new OutputFailingProgress(
            failingReport: "Backtest run created",
            failingOperation: null,
            CreateOutputException(objectDisposed));
        var engine = new BacktestEngine(_runRepository, new StubDayProcessor(), progress);

        var result = await engine.RunAsync(
            BuildRequest(new DateTime(2024, 4, 1), new DateTime(2024, 4, 1)));

        Assert.Equal(BacktestRunStatus.Completed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Completed, saved.Status);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PersistsFailureWhenFailureProgressOutputFails(bool objectDisposed)
    {
        var date = new DateTime(2024, 5, 1);
        var progress = new OutputFailingProgress(
            failingReport: "Simulation failed",
            failingOperation: "Marking backtest failed",
            CreateOutputException(objectDisposed));
        var engine = new BacktestEngine(
            _runRepository,
            new ThrowingDayProcessor(date),
            progress);

        var result = await engine.RunAsync(BuildRequest(date, date));

        Assert.Equal(BacktestRunStatus.Failed, result.Status);
        Assert.Equal("Simulated day processing failure.", result.ErrorMessage);

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
    public async Task RunAsync_FinalMetricsMatchLastSnapshotAndPersistedTotalsAtRoundingMidpoint()
    {
        var startDate = new DateTime(2024, 6, 1);
        const decimal initialCapital = 100m;
        const decimal endingValue = 99.99995m;
        var processor = new RoundingBoundaryDayProcessor(endingValue);
        var engine = BuildEngine(processor);

        var result = await engine.RunAsync(BuildRequest(startDate, startDate, initialCapital));

        var snapshot = Assert.IsType<BacktestSnapshot>(processor.LastSnapshot);
        Assert.Equal(100.0000m, snapshot.PortfolioValue);
        Assert.Equal(-0.0001m, snapshot.ProfitLoss);
        Assert.Equal(-0.0001m, snapshot.ReturnPercent);
        Assert.Equal(BacktestRunStatus.Completed, result.Status);
        Assert.Equal(snapshot.PortfolioValue, result.FinalCapital);
        Assert.Equal(snapshot.ProfitLoss, result.TotalProfit);
        Assert.Equal(snapshot.ReturnPercent, result.TotalReturnPercent);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(snapshot.PortfolioValue, saved.FinalCapital);
        Assert.Equal(snapshot.ProfitLoss, saved.TotalProfit);
        Assert.Equal(snapshot.ReturnPercent, saved.TotalReturnPercent);
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

    private sealed class RoundingBoundaryDayProcessor : IBacktestDayProcessor
    {
        private readonly decimal _endingValue;

        public RoundingBoundaryDayProcessor(decimal endingValue)
        {
            _endingValue = endingValue;
        }

        public BacktestSnapshot? LastSnapshot { get; private set; }

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            var portfolio = request.Portfolio;
            portfolio.Cash = _endingValue;
            var profitLoss = portfolio.TotalValue - portfolio.StartingCash;
            var returnPercent = portfolio.StartingCash > 0m
                ? profitLoss / portfolio.StartingCash * 100m
                : 0m;
            var snapshot = new BacktestSnapshot
            {
                BacktestRunId = request.BacktestRunId,
                Date = request.CurrentDate.Date,
                PortfolioValue = TradingPrecisionPolicy.RoundReport(portfolio.TotalValue),
                Cash = TradingPrecisionPolicy.RoundReport(portfolio.Cash),
                ProfitLoss = TradingPrecisionPolicy.RoundReport(profitLoss),
                ReturnPercent = TradingPrecisionPolicy.RoundReport(returnPercent)
            };
            LastSnapshot = snapshot;

            return Task.FromResult(new BacktestDayResult
            {
                CurrentDate = request.CurrentDate.Date,
                Portfolio = portfolio,
                Snapshot = snapshot,
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

    private static Exception CreateOutputException(bool objectDisposed) =>
        objectDisposed
            ? new ObjectDisposedException("progress")
            : new IOException("Progress output is unavailable.");

    private sealed class OutputFailingProgress(
        string? failingReport,
        string? failingOperation,
        Exception exception) : IWorkflowProgress
    {
        public WorkflowProgressContext Context { get; set; } = new();

        public void Report(string message, bool force = false)
        {
            if (message == failingReport)
                throw exception;
        }

        public IDisposable BeginOperation(string operation, string? symbol = null)
        {
            if (operation == failingOperation)
                throw exception;

            return NoopScope.Instance;
        }

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
