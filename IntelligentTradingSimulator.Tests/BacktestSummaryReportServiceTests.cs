using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.App.Reporting;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
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

    [Fact]
    public async Task GenerateAsync_ReturnsSummaryForCompletedRun()
    {
        var run = await CreateCompletedRunAsync(
            name: "Momentum Q1",
            startDate: new DateTime(2024, 1, 1),
            endDate: new DateTime(2024, 1, 3),
            initialCapital: 10_000m,
            finalCapital: 10_850m,
            totalProfit: 850m,
            totalReturnPercent: 8.5m);

        await _snapshotRepository.AddRangeAsync(
        [
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 1),
                PortfolioValue = 10_100m,
                Cash = 2_000m,
                ProfitLoss = 100m,
                ReturnPercent = 1m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = 10_500m,
                Cash = 1_900m,
                ProfitLoss = 500m,
                ReturnPercent = 5m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 3),
                PortfolioValue = 10_850m,
                Cash = 1_800m,
                ProfitLoss = 850m,
                ReturnPercent = 8.5m
            }
        ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Report);
        Assert.Equal(run.Id, result.Report.RunId);
        Assert.Equal("Momentum Q1", result.Report.Name);
        Assert.Equal(new DateTime(2024, 1, 1), result.Report.StartDate);
        Assert.Equal(new DateTime(2024, 1, 3), result.Report.EndDate);
        Assert.Equal(10_000m, result.Report.InitialCapital);
        Assert.Equal(10_850m, result.Report.FinalCapital);
        Assert.Equal(850m, result.Report.TotalProfit);
        Assert.Equal(8.5m, result.Report.TotalReturnPercent);
        Assert.Equal(3, result.Report.ProcessedDays);
    }

    [Fact]
    public async Task GenerateAsync_CalculatesBestAndWorstDayReturns()
    {
        var run = await CreateCompletedRunAsync(
            name: "Momentum Q2",
            startDate: new DateTime(2024, 4, 1),
            endDate: new DateTime(2024, 4, 3),
            initialCapital: 5_000m,
            finalCapital: 5_250m,
            totalProfit: 250m,
            totalReturnPercent: 5m);

        await _snapshotRepository.AddRangeAsync(
        [
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 4, 3),
                PortfolioValue = 5_250m,
                Cash = 900m,
                ProfitLoss = 250m,
                ReturnPercent = 3.25m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 4, 1),
                PortfolioValue = 4_900m,
                Cash = 1_200m,
                ProfitLoss = -100m,
                ReturnPercent = -2m
            },
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 4, 2),
                PortfolioValue = 5_100m,
                Cash = 1_000m,
                ProfitLoss = 100m,
                ReturnPercent = 1.5m
            }
        ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Report);
        Assert.Equal(3.25m, result.Report.BestDayReturnPercent);
        Assert.Equal(-2m, result.Report.WorstDayReturnPercent);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsErrorWhenRunDoesNotExist()
    {
        var result = await _service.GenerateAsync(999);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Report);
        Assert.Equal("Backtest run 999 was not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsErrorWhenSnapshotsAreAbsent()
    {
        var run = await CreateCompletedRunAsync(
            name: "No Snapshots",
            startDate: new DateTime(2024, 1, 1),
            endDate: new DateTime(2024, 1, 3),
            initialCapital: 10_000m,
            finalCapital: 10_000m,
            totalProfit: 0m,
            totalReturnPercent: 0m);

        var result = await _service.GenerateAsync(run.Id);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Report);
        Assert.Equal(
            $"No backtest snapshots were found for run {run.Id}.",
            result.ErrorMessage);
    }

    private async Task<BacktestRun> CreateCompletedRunAsync(
        string name,
        DateTime startDate,
        DateTime endDate,
        decimal initialCapital,
        decimal finalCapital,
        decimal totalProfit,
        decimal totalReturnPercent)
    {
        var run = new BacktestRun
        {
            Name = name,
            StartDate = startDate,
            EndDate = endDate,
            InitialCapital = initialCapital,
            FinalCapital = finalCapital,
            TotalProfit = totalProfit,
            TotalReturnPercent = totalReturnPercent,
            Status = BacktestRunStatus.Completed,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        return run;
    }
}
