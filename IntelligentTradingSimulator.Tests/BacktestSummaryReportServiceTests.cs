using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSummaryReportServiceTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;
    private readonly BacktestSnapshotRepository _snapshotRepository;
    private readonly BacktestSummaryReportService _service;

    public BacktestSummaryReportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
        _snapshotRepository = new BacktestSnapshotRepository(_dbContext);
        _service = new BacktestSummaryReportService(_runRepository, _snapshotRepository);
    }

    public void Dispose() => _dbContext.Dispose();

    private async Task<BacktestRun> CreateRunAsync(
        decimal initialCapital = 10_000m,
        decimal finalCapital = 11_000m)
    {
        var run = new BacktestRun
        {
            Name = "Test Run",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 1, 31),
            InitialCapital = initialCapital,
            FinalCapital = finalCapital,
            TotalProfit = finalCapital - initialCapital,
            TotalReturnPercent = (finalCapital - initialCapital) / initialCapital * 100m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);
        return run;
    }

    [Fact]
    public async Task GenerateAsync_ReturnsError_WhenRunNotFound()
    {
        var result = await _service.GenerateAsync(999);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsError_WhenNoSnapshotsExist()
    {
        var run = await CreateRunAsync();

        var result = await _service.GenerateAsync(run.Id);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateAsync_SumsTradesExecutedAcrossAllSnapshots()
    {
        var run = await CreateRunAsync();

        var snapshots = new List<BacktestSnapshot>
        {
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = 10_200m,
                Cash = 8_000m,
                TradesExecuted = 3,
                RebalanceExecuted = true
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 3),
                PortfolioValue = 10_300m,
                Cash = 8_000m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 4),
                PortfolioValue = 10_500m,
                Cash = 7_500m,
                TradesExecuted = 2,
                RebalanceExecuted = true
            }
        };

        await _snapshotRepository.AddRangeAsync(snapshots);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.TradesCount);
    }

    [Fact]
    public async Task GenerateAsync_CountsRebalancesFromRebalanceExecutedFlag()
    {
        var run = await CreateRunAsync();

        var snapshots = new List<BacktestSnapshot>
        {
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = 10_100m,
                Cash = 9_000m,
                TradesExecuted = 1,
                RebalanceExecuted = true
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 3),
                PortfolioValue = 10_100m,
                Cash = 9_000m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 4),
                PortfolioValue = 10_200m,
                Cash = 8_800m,
                TradesExecuted = 1,
                RebalanceExecuted = true
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 5),
                PortfolioValue = 10_200m,
                Cash = 8_800m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            }
        };

        await _snapshotRepository.AddRangeAsync(snapshots);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.RebalancesCount);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsZeroCounts_WhenNoTradesOrRebalancesOccurred()
    {
        var run = await CreateRunAsync();

        var snapshots = new List<BacktestSnapshot>
        {
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = 10_000m,
                Cash = 10_000m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 3),
                PortfolioValue = 10_000m,
                Cash = 10_000m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            }
        };

        await _snapshotRepository.AddRangeAsync(snapshots);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.TradesCount);
        Assert.Equal(0, result.RebalancesCount);
    }

    [Fact]
    public async Task GenerateAsync_CountsMultipleTradesPerDay()
    {
        var run = await CreateRunAsync();

        // A single day with 5 trades executed (e.g. one rebalance touching 5 symbols)
        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 2),
            PortfolioValue = 10_500m,
            Cash = 5_000m,
            TradesExecuted = 5,
            RebalanceExecuted = true
        });

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.TradesCount);
        Assert.Equal(1, result.RebalancesCount);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsCorrectFinancialSummaryFields()
    {
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 12_000m);

        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 12_000m,
            Cash = 4_000m,
            TradesExecuted = 2,
            RebalanceExecuted = true
        });

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(run.Id, result.BacktestRunId);
        Assert.Equal("Test Run", result.RunName);
        Assert.Equal(10_000m, result.StartCapital);
        Assert.Equal(12_000m, result.FinalCapital);
        Assert.Equal(2_000m, result.Profit);
        Assert.Equal(20m, result.ReturnPercent);
    }

    [Fact]
    public async Task GenerateAsync_TradesCountIsNotEqualToRebalancesCount_WhenMultipleTradesPerRebalance()
    {
        var run = await CreateRunAsync();

        // Two rebalance days: first day has 4 trades, second has 3 trades
        var snapshots = new List<BacktestSnapshot>
        {
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = 10_200m,
                Cash = 6_000m,
                TradesExecuted = 4,
                RebalanceExecuted = true
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 3),
                PortfolioValue = 10_100m,
                Cash = 6_500m,
                TradesExecuted = 0,
                RebalanceExecuted = false
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 4),
                PortfolioValue = 10_400m,
                Cash = 5_500m,
                TradesExecuted = 3,
                RebalanceExecuted = true
            }
        };

        await _snapshotRepository.AddRangeAsync(snapshots);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.TradesCount);
        Assert.Equal(2, result.RebalancesCount);
        Assert.NotEqual(result.TradesCount, result.RebalancesCount);
    }
}
