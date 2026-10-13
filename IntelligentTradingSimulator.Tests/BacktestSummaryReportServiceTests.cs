using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSummaryReportServiceTests : IDisposable
{
    private readonly TradingSimulatorDbContext _dbContext;
    private readonly BacktestRunRepository _runRepository;
    private readonly BacktestSnapshotRepository _snapshotRepository;
    private readonly FakeBacktestBenchmarkService _benchmarkService;
    private readonly BacktestSummaryReportService _service;

    public BacktestSummaryReportServiceTests()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new TradingSimulatorDbContext(options);
        _runRepository = new BacktestRunRepository(_dbContext);
        _snapshotRepository = new BacktestSnapshotRepository(_dbContext);
        _benchmarkService = new FakeBacktestBenchmarkService();
        _service = new BacktestSummaryReportService(
            _runRepository,
            _snapshotRepository,
            _benchmarkService);
    }

    public void Dispose() => _dbContext.Dispose();

    private async Task<BacktestRun> CreateRunAsync(
        decimal initialCapital = 10_000m,
        decimal finalCapital = 11_000m)
    {
        var run = new BacktestRun
        {
            Name = "Test Run",
            StrategyId = "default",
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
    public async Task GenerateAsync_ExportsPersistedRunSummaryWithRunDates()
    {
        var run = await CreateRunAsync();
        run.Status = BacktestRunStatus.Completed;
        await _runRepository.UpdateAsync(run);
        await _snapshotRepository.AddRangeAsync(
        [
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = run.InitialCapital
            }
        ]);

        var summary = await _service.GenerateAsync(run.Id);
        Assert.True(summary.IsSuccess);
        Assert.Equal(BacktestRunStatus.Completed, summary.Status);
        Assert.Equal(run.StartDate, summary.StartDate);
        Assert.Equal(run.EndDate, summary.EndDate);

        using var writer = new System.IO.StringWriter();
        await new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]);
        Assert.Contains(
            $"{run.Id},Test Run,default,2024-01-01,2024-01-31,10000,11000,1000,10,0,0,,0,0,,,,,\r\n",
            writer.ToString());
    }

    [Fact]
    public async Task GenerateAsync_PartialSnapshotsDoNotAllowExportOfIncompleteRun()
    {
        var run = await CreateRunAsync();
        await _snapshotRepository.AddRangeAsync(
        [
            new BacktestSnapshot
            {
                BacktestRunId = run.Id,
                Date = new DateTime(2024, 1, 2),
                PortfolioValue = run.InitialCapital
            }
        ]);

        foreach (var status in new[] { BacktestRunStatus.Running, BacktestRunStatus.Failed })
        {
            run.Status = status;
            await _runRepository.UpdateAsync(run);
            var summary = await _service.GenerateAsync(run.Id);
            Assert.True(summary.IsSuccess);
            Assert.Equal(status, summary.Status);

            using var writer = new System.IO.StringWriter();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                new BacktestSummaryCsvExporter().WriteAsync(writer, [summary]));
            Assert.Empty(writer.ToString());
        }
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
    public async Task GenerateAsync_CalculatesMaxDrawdownFromSnapshotPortfolioValues()
    {
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 10_700m);

        await _snapshotRepository.AddRangeAsync(
            [
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 10_000m,
                    Cash = 4_000m,
                    TradesExecuted = 1,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 3),
                    PortfolioValue = 12_000m,
                    Cash = 4_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 4),
                    PortfolioValue = 9_000m,
                    Cash = 3_000m,
                    TradesExecuted = 2,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 5),
                    PortfolioValue = 10_700m,
                    Cash = 3_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(25m, result.MaxDrawdownPercent);
        // Peak Jan 3 (12,000) → trough Jan 4 (9,000): 1 calendar day
        Assert.Equal(1, result.DrawdownDurationDays);
        // Jan 5 value is 10,700 < 12,000 peak, so not recovered
        Assert.Null(result.RecoveryTimeDays);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsZeroMaxDrawdown_WhenPortfolioValueNeverDrops()
    {
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 11_500m);

        await _snapshotRepository.AddRangeAsync(
            [
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 10_000m,
                    Cash = 5_000m,
                    TradesExecuted = 1,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 3),
                    PortfolioValue = 10_750m,
                    Cash = 4_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 4),
                    PortfolioValue = 11_500m,
                    Cash = 4_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.MaxDrawdownPercent);
        Assert.Equal(0, result.DrawdownDurationDays);
        Assert.Null(result.RecoveryTimeDays);
    }

    [Fact]
    public async Task GenerateAsync_CalculatesDrawdownDuration_WhenPortfolioDrops()
    {
        // Peak on Jan 3 (12,000), trough on Jan 5 (9,000) => duration = 2 days
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 9_000m);

        await _snapshotRepository.AddRangeAsync(
            [
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 10_000m,
                    Cash = 4_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 3),
                    PortfolioValue = 12_000m,
                    Cash = 4_500m,
                    TradesExecuted = 1,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 4),
                    PortfolioValue = 10_500m,
                    Cash = 3_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 5),
                    PortfolioValue = 9_000m,
                    Cash = 3_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(25m, result.MaxDrawdownPercent);
        // Peak Jan 3 → trough Jan 5: 2 calendar days
        Assert.Equal(2, result.DrawdownDurationDays);
        // No snapshot recovers to 12,000 after Jan 5
        Assert.Null(result.RecoveryTimeDays);
    }

    [Fact]
    public async Task GenerateAsync_CalculatesRecoveryTime_WhenPortfolioRecoversToPeak()
    {
        // Peak on Jan 3 (12,000), trough on Jan 4 (9,000), recovered on Jan 6 (12,000)
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 12_000m);

        await _snapshotRepository.AddRangeAsync(
            [
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 10_000m,
                    Cash = 4_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 3),
                    PortfolioValue = 12_000m,
                    Cash = 4_500m,
                    TradesExecuted = 1,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 4),
                    PortfolioValue = 9_000m,
                    Cash = 3_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 5),
                    PortfolioValue = 10_500m,
                    Cash = 3_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 6),
                    PortfolioValue = 12_000m,
                    Cash = 4_000m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(25m, result.MaxDrawdownPercent);
        // Peak Jan 3 → trough Jan 4: 1 calendar day
        Assert.Equal(1, result.DrawdownDurationDays);
        // Trough Jan 4 → recovered Jan 6: 2 calendar days
        Assert.Equal(2, result.RecoveryTimeDays);
    }

    [Fact]
    public async Task GenerateAsync_RecoveryTimeDays_IsNullWhenNotFullyRecovered()
    {
        // Partial recovery but never reaches peak
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 10_700m);

        await _snapshotRepository.AddRangeAsync(
            [
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 10_000m,
                    Cash = 4_000m,
                    TradesExecuted = 1,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 3),
                    PortfolioValue = 12_000m,
                    Cash = 4_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 4),
                    PortfolioValue = 9_000m,
                    Cash = 3_000m,
                    TradesExecuted = 2,
                    RebalanceExecuted = true
                },
                new BacktestSnapshot
                {
                    BacktestRunId = run.Id,
                    Date = new DateTime(2024, 1, 5),
                    PortfolioValue = 10_700m,
                    Cash = 3_500m,
                    TradesExecuted = 0,
                    RebalanceExecuted = false
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(25m, result.MaxDrawdownPercent);
        // Peak Jan 3 → trough Jan 4: 1 day
        Assert.Equal(1, result.DrawdownDurationDays);
        // 10,700 < 12,000 peak, so not recovered
        Assert.Null(result.RecoveryTimeDays);
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

    [Fact]
    public async Task GenerateAsync_ReturnsBenchmarkComparisonsWithOutcomes_ForRun()
    {
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 12_000m);

        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 12_000m,
            Cash = 4_000m,
            TradesExecuted = 1,
            RebalanceExecuted = true
        });

        _benchmarkService.SetResults(
            run.Id,
            [
                new BenchmarkResult
                {
                    Symbol = "SPY",
                    StartPrice = 100m,
                    EndPrice = 110m,
                    ReturnPercent = 10m
                },
                new BenchmarkResult
                {
                    Symbol = "QQQ",
                    StartPrice = 200m,
                    EndPrice = 230m,
                    ReturnPercent = 15m
                },
                new BenchmarkResult
                {
                    Symbol = "IWM",
                    StartPrice = 50m,
                    EndPrice = 60m,
                    ReturnPercent = 20m
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.BenchmarkComparisons.Count);
        Assert.Contains(
            result.BenchmarkComparisons,
            x => x.Symbol == "SPY" &&
                 x.StrategyReturnPercent == 20m &&
                 x.BenchmarkReturnPercent == 10m &&
                 x.RelativeReturnPercent == 10m &&
                 x.AlphaPercent == 10m &&
                 x.Outcome == BacktestBenchmarkComparisonOutcome.Outperform);
        Assert.Contains(
            result.BenchmarkComparisons,
            x => x.Symbol == "QQQ" &&
                 x.StrategyReturnPercent == 20m &&
                 x.BenchmarkReturnPercent == 15m &&
                 x.RelativeReturnPercent == 5m &&
                 x.AlphaPercent == 5m &&
                 x.Outcome == BacktestBenchmarkComparisonOutcome.Outperform);
        Assert.Contains(
            result.BenchmarkComparisons,
            x => x.Symbol == "IWM" &&
                 x.StrategyReturnPercent == 20m &&
                 x.BenchmarkReturnPercent == 20m &&
                 x.RelativeReturnPercent == 0m &&
                 x.AlphaPercent == 0m &&
                 x.Outcome == BacktestBenchmarkComparisonOutcome.Match);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsUnderperformOutcome_WhenBenchmarkReturnIsHigher()
    {
        var run = await CreateRunAsync(initialCapital: 10_000m, finalCapital: 11_000m);

        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 11_000m,
            Cash = 4_000m,
            TradesExecuted = 1,
            RebalanceExecuted = true
        });

        _benchmarkService.SetResults(
            run.Id,
            [
                new BenchmarkResult
                {
                    Symbol = "SPY",
                    StartPrice = 100m,
                    EndPrice = 115m,
                    ReturnPercent = 15m
                }
            ]);

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Single(result.BenchmarkComparisons);
        Assert.Equal(-5m, result.BenchmarkComparisons[0].RelativeReturnPercent);
        Assert.Equal(-5m, result.BenchmarkComparisons[0].AlphaPercent);
        Assert.Equal(
            BacktestBenchmarkComparisonOutcome.Underperform,
            result.BenchmarkComparisons[0].Outcome);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsEmptyBenchmarkComparisons_WhenBenchmarkDataUnavailable()
    {
        var run = await CreateRunAsync();

        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 11_000m,
            Cash = 4_000m,
            TradesExecuted = 1,
            RebalanceExecuted = true
        });

        _benchmarkService.SetFailure(run.Id, "Benchmark data unavailable.");

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.BenchmarkComparisons);
    }

    [Fact]
    public async Task GenerateAsync_PopulatesStrategyIdFromBacktestRun()
    {
        var run = new BacktestRun
        {
            Name = "Strategy Identity Test",
            StrategyId = "momentum-capped-v2",
            StartDate = new DateTime(2024, 1, 1),
            EndDate = new DateTime(2024, 1, 31),
            InitialCapital = 10_000m,
            FinalCapital = 11_000m,
            TotalProfit = 1_000m,
            TotalReturnPercent = 10m,
            CreatedAt = DateTime.UtcNow
        };

        await _runRepository.AddAsync(run);

        await _snapshotRepository.AddAsync(new BacktestSnapshot
        {
            BacktestRunId = run.Id,
            Date = new DateTime(2024, 1, 15),
            PortfolioValue = 11_000m,
            Cash = 5_000m,
            TradesExecuted = 1,
            RebalanceExecuted = true
        });

        var result = await _service.GenerateAsync(run.Id);

        Assert.True(result.IsSuccess);
        Assert.Equal("momentum-capped-v2", result.StrategyId);
    }

    private sealed class FakeBacktestBenchmarkService : IBacktestBenchmarkService
    {
        private readonly Dictionary<int, BacktestBenchmarkResult> _responses = new();
        private readonly List<string> _requestedSymbols = [];

        public IReadOnlyList<string> RequestedSymbols => _requestedSymbols;

        public void SetResults(int backtestRunId, IReadOnlyList<BenchmarkResult> results)
        {
            _responses[backtestRunId] = new BacktestBenchmarkResult
            {
                IsSuccess = true,
                BacktestRunId = backtestRunId,
                BenchmarkResults = results
            };
        }

        public void SetFailure(int backtestRunId, string errorMessage)
        {
            _responses[backtestRunId] = new BacktestBenchmarkResult
            {
                IsSuccess = false,
                BacktestRunId = backtestRunId,
                ErrorMessage = errorMessage,
                BenchmarkResults = []
            };
        }

        public Task<BacktestBenchmarkResult> GetForRunAsync(int backtestRunId)
        {
            if (_responses.TryGetValue(backtestRunId, out var response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(new BacktestBenchmarkResult
            {
                IsSuccess = true,
                BacktestRunId = backtestRunId,
                BenchmarkResults = []
            });
        }

        public Task<BacktestBenchmarkResult> GetForRunAsync(
            int backtestRunId,
            IEnumerable<string> symbols)
        {
            _requestedSymbols.Clear();
            _requestedSymbols.AddRange(symbols);
            return GetForRunAsync(backtestRunId);
        }
    }
}
