using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestPersistenceTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;
    private readonly BacktestSnapshotRepository _snapshotRepository;

    public BacktestPersistenceTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
        _snapshotRepository = new BacktestSnapshotRepository(_dbContext);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }

    [Fact]
    public async Task BacktestRun_CanBeSavedAndLoadedById()
    {
        var run = new BacktestRun
        {
            Name = "Test Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 6, 30),
            InitialCapital = 100_000m,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        var loaded = await _runRepository.GetByIdAsync(run.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Test Run", loaded.Name);
        Assert.Equal(new DateTime(2024, 1, 1), loaded.StartDate);
        Assert.Equal(new DateTime(2024, 6, 30), loaded.EndDate);
        Assert.Equal(100_000m, loaded.InitialCapital);
    }

    [Fact]
    public async Task BacktestRun_CanBeUpdatedWithFinalValues()
    {
        var run = new BacktestRun
        {
            Name = "Update Test Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 12, 31),
            InitialCapital = 50_000m,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        run.FinalCapital = 60_000m;
        run.TotalProfit = 10_000m;
        run.TotalReturnPercent = 20m;

        await _runRepository.UpdateAsync(run);

        var loaded = await _runRepository.GetByIdAsync(run.Id);

        Assert.NotNull(loaded);
        Assert.Equal(60_000m, loaded.FinalCapital);
        Assert.Equal(10_000m, loaded.TotalProfit);
        Assert.Equal(20m, loaded.TotalReturnPercent);
    }

    [Fact]
    public async Task BacktestSnapshot_CanBeSavedForARun()
    {
        var run = new BacktestRun
        {
            Name = "Snapshot Test Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 3, 31),
            InitialCapital = 10_000m,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        var snapshot = new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 10_500m,
            Cash = 5_000m,
            ProfitLoss = 500m,
            ReturnPercent = 5m
        };

        await _snapshotRepository.AddAsync(snapshot);

        var loaded = await _snapshotRepository.GetByRunIdAsync(run.Id);

        Assert.Single(loaded);
        Assert.Equal(run.Id, loaded[0].BacktestRunId);
        Assert.Equal(new DateTime(2024, 1, 15), loaded[0].Date);
        Assert.Equal(10_500m, loaded[0].PortfolioValue);
        Assert.Equal(5m, loaded[0].ReturnPercent);
    }

    [Fact]
    public async Task BacktestSnapshots_CanBeLoadedForARunOrderedByDate()
    {
        var run = new BacktestRun
        {
            Name = "Multi-Snapshot Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 3, 31),
            InitialCapital = 10_000m,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        var snapshots = new List<BacktestSnapshot>
        {
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 31),
                PortfolioValue = 10_200m,
                Cash = 4_000m,
                ProfitLoss = 200m,
                ReturnPercent = 2m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 15),
                PortfolioValue = 10_100m,
                Cash = 4_500m,
                ProfitLoss = 100m,
                ReturnPercent = 1m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 2, 28),
                PortfolioValue = 10_500m,
                Cash = 3_000m,
                ProfitLoss = 500m,
                ReturnPercent = 5m
            }
        };

        await _snapshotRepository.AddRangeAsync(snapshots);

        var loaded = await _snapshotRepository.GetByRunIdAsync(run.Id);

        Assert.Equal(3, loaded.Count);
        Assert.Equal(new DateTime(2024, 1, 15), loaded[0].Date);
        Assert.Equal(new DateTime(2024, 1, 31), loaded[1].Date);
        Assert.Equal(new DateTime(2024, 2, 28), loaded[2].Date);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenRunDoesNotExist()
    {
        var loaded = await _runRepository.GetByIdAsync(999);

        Assert.Null(loaded);
    }

    [Fact]
    public async Task GetByRunIdAsync_ReturnsEmpty_WhenNoSnapshotsExist()
    {
        var run = new BacktestRun
        {
            Name = "Empty Snapshots Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 3, 31),
            InitialCapital = 10_000m,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        var loaded = await _snapshotRepository.GetByRunIdAsync(run.Id);

        Assert.Empty(loaded);
    }
}
