using System;
using System.IO;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngine : IBacktestEngine
{
    private readonly IBacktestRunRepository _runRepository;
    private readonly IBacktestDayProcessor _dayProcessor;
    private readonly IWorkflowProgress _progress;

    public BacktestEngine(
        IBacktestRunRepository runRepository,
        IBacktestDayProcessor dayProcessor,
        IWorkflowProgress? progress = null)
    {
        _runRepository = runRepository;
        _dayProcessor = dayProcessor;
        _progress = progress ?? NullWorkflowProgress.Instance;
    }

    public async Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
    {
        if (!TradingPrecisionPolicy.CashBalanceIsSupported(request.InitialCapital))
        {
            throw new ArgumentOutOfRangeException(nameof(request.InitialCapital),
                "Initial capital must be within the supported cash range and exactly representable at eight decimal places.");
        }

        _progress.Context = _progress.Context with
        {
            Workflow = "backtest",
            StrategyId = request.StrategyId,
            RunId = null,
            Date = null,
            CompletedDates = 0,
            TotalDates = Math.Max(0, (request.EndDate.Date - request.StartDate.Date).Days + 1),
            CompletedSymbols = null,
            TotalSymbols = null
        };
        var run = new BacktestRun
        {
            Name = request.Name,
            StrategyId = request.StrategyId,
            StartDate = request.StartDate.Date,
            EndDate = request.EndDate.Date,
            InitialCapital = request.InitialCapital,
            FinalCapital = 0m,
            TotalProfit = 0m,
            TotalReturnPercent = 0m,
            Status = BacktestRunStatus.Running,
            CreatedAt = DateTime.UtcNow
        };

        using (_progress.BeginOperation("Creating backtest run"))
        {
            await _runRepository.AddAsync(run);
        }
        _progress.Context = _progress.Context with { RunId = run.Id };
        ReportBestEffort("Backtest run created");

        var portfolio = new Portfolio
        {
            Name = request.Name,
            StartingCash = request.InitialCapital,
            Cash = request.InitialCapital
        };

        BacktestEngineResult result;
        try
        {
            var currentDate = request.StartDate.Date;
            var endDate = request.EndDate.Date;

            while (currentDate <= endDate)
            {
                _progress.Context = _progress.Context with
                {
                    Date = currentDate,
                    CompletedSymbols = null,
                    TotalSymbols = null
                };
                _progress.Report("Processing simulation date");
                var dayRequest = new BacktestDayRequest
                {
                    BacktestRunId = run.Id,
                    CurrentDate = currentDate,
                    HistoricalStartDate = request.HistoricalStartDate,
                    StrategyConfiguration = request.StrategyConfiguration,
                    Portfolio = portfolio,
                    Symbols = request.Symbols
                };

                BacktestDayResult dayResult;
                using (_progress.BeginOperation("Processing simulated dates"))
                {
                    dayResult = await _dayProcessor.ProcessAsync(dayRequest);
                }
                portfolio = dayResult.Portfolio;
                _progress.Context = _progress.Context with
                {
                    CompletedDates = (_progress.Context.CompletedDates ?? 0) + 1
                };
                _progress.Report("Simulation date complete");
                currentDate = currentDate.AddDays(1);
            }

            var finalValue = portfolio.TotalValue;
            run.FinalCapital = TradingPrecisionPolicy.RoundReport(finalValue);
            run.TotalProfit = TradingPrecisionPolicy.RoundReport(finalValue - request.InitialCapital);
            run.TotalReturnPercent = request.InitialCapital > 0m
                ? TradingPrecisionPolicy.RoundReport(
                    (finalValue - request.InitialCapital) / request.InitialCapital * 100m)
                : 0m;
            run.Status = BacktestRunStatus.Completed;
            run.Portfolio = portfolio;

            using (_progress.BeginOperation("Saving final backtest result"))
            {
                await _runRepository.UpdateAsync(run);
            }

            result = new BacktestEngineResult
            {
                RunId = run.Id,
                Status = BacktestRunStatus.Completed,
                FinalCapital = run.FinalCapital,
                TotalProfit = run.TotalProfit,
                TotalReturnPercent = run.TotalReturnPercent
            };
        }
        catch (Exception ex)
        {
            using (BeginOperationBestEffort("Marking backtest failed"))
            {
                await _runRepository.MarkFailedAsync(run.Id);
            }
            ReportBestEffort("Simulation failed");

            return new BacktestEngineResult
            {
                RunId = run.Id,
                Status = BacktestRunStatus.Failed,
                FinalCapital = 0m,
                TotalProfit = 0m,
                TotalReturnPercent = 0m,
                ErrorMessage = ex.Message
            };
        }

        ReportBestEffort("Simulation complete");

        return result;
    }

    private IDisposable BeginOperationBestEffort(string operation)
    {
        try
        {
            return new BestEffortProgressScope(_progress.BeginOperation(operation));
        }
        catch (IOException)
        {
            return NoopProgressScope.Instance;
        }
        catch (ObjectDisposedException)
        {
            return NoopProgressScope.Instance;
        }
    }

    private void ReportBestEffort(string message)
    {
        try
        {
            _progress.Report(message, force: true);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class BestEffortProgressScope(IDisposable scope) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                scope.Dispose();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class NoopProgressScope : IDisposable
    {
        public static NoopProgressScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
