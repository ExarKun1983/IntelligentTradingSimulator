using System;
using System.Collections.Generic;
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
        var engine = new BacktestEngine(repository, new StubDayProcessor());

        var result = await engine.RunAsync(
            BuildRequest(new DateTime(2024, 5, 1), new DateTime(2024, 5, 1)));

        Assert.Equal(BacktestRunStatus.Failed, result.Status);

        var saved = await _runRepository.GetByIdAsync(result.RunId);
        Assert.NotNull(saved);
        Assert.Equal(BacktestRunStatus.Failed, saved.Status);
        Assert.Null(saved.Portfolio);
        Assert.Empty(await _dbContext.Portfolios.ToListAsync());
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
}
