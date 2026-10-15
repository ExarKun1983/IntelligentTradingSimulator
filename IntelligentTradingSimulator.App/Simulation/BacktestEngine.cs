using System;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngine : IBacktestEngine
{
    private const int FinancialPrecision = 4;

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
        _progress.Report("Backtest run created", force: true);

        var portfolio = new Portfolio
        {
            Name = request.Name,
            StartingCash = request.InitialCapital,
            Cash = request.InitialCapital
        };

        try
        {
            var currentDate = request.StartDate.Date;
            var endDate = request.EndDate.Date;
            BacktestDayResult? lastDayResult = null;

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
                lastDayResult = dayResult;

                portfolio = dayResult.Portfolio;
                _progress.Context = _progress.Context with
                {
                    CompletedDates = (_progress.Context.CompletedDates ?? 0) + 1
                };
                _progress.Report("Simulation date complete");
                currentDate = currentDate.AddDays(1);
            }

            var finalValue = lastDayResult?.Snapshot.PortfolioValue ?? portfolio.TotalValue;
            run.FinalCapital = Math.Round(finalValue, FinancialPrecision);
            run.TotalProfit = Math.Round(finalValue - request.InitialCapital, FinancialPrecision);
            run.TotalReturnPercent = request.InitialCapital > 0m
                ? Math.Round((finalValue - request.InitialCapital) / request.InitialCapital * 100m, FinancialPrecision)
                : 0m;
            run.Status = BacktestRunStatus.Completed;
            run.Portfolio = portfolio;

            using (_progress.BeginOperation("Saving final backtest result"))
            {
                await _runRepository.UpdateAsync(run);
            }
            _progress.Report("Simulation complete", force: true);

            return new BacktestEngineResult
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
            using (_progress.BeginOperation("Marking backtest failed"))
            {
                await _runRepository.MarkFailedAsync(run.Id);
            }
            _progress.Report("Simulation failed", force: true);

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
    }
}
