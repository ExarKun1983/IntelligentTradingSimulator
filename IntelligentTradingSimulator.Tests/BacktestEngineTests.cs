using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    private static BacktestEngineRequest BuildRequest(
        DateTime startDate,
        DateTime endDate,
        decimal initialCapital = 10_000m)
        => new BacktestEngineRequest
        {
            Name = "Test Run",
            StartDate = startDate,
            EndDate = endDate,
            HistoricalStartDate = startDate.AddDays(-30),
            InitialCapital = initialCapital,
            Symbols = ["AAPL", "MSFT"]
        };

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
        Assert.Equal(startDate.Date, saved.StartDate);
        Assert.Equal(endDate.Date, saved.EndDate);
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

    // ── Stubs ──────────────────────────────────────────────────────────────

    private sealed class StubDayProcessor : IBacktestDayProcessor
    {
        public int CallCount { get; private set; }
        public DateTime FirstRequestDate { get; private set; }
        public DateTime LastRequestDate { get; private set; }

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            if (CallCount == 0)
                FirstRequestDate = request.CurrentDate.Date;

            CallCount++;
            LastRequestDate = request.CurrentDate.Date;

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
}
