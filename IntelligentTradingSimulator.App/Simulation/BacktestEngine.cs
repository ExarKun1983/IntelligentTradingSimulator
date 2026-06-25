using System;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngine
{
    private const int FinancialPrecision = 4;

    private readonly IBacktestRunRepository _runRepository;
    private readonly IBacktestDayProcessor _dayProcessor;

    public BacktestEngine(
        IBacktestRunRepository runRepository,
        IBacktestDayProcessor dayProcessor)
    {
        _runRepository = runRepository;
        _dayProcessor = dayProcessor;
    }

    public async Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request)
    {
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

        await _runRepository.AddAsync(run);

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
                var dayRequest = new BacktestDayRequest
                {
                    BacktestRunId = run.Id,
                    CurrentDate = currentDate,
                    HistoricalStartDate = request.HistoricalStartDate,
                    Portfolio = portfolio,
                    Symbols = request.Symbols
                };

                var dayResult = await _dayProcessor.ProcessAsync(dayRequest);
                lastDayResult = dayResult;

                portfolio = dayResult.Portfolio;
                currentDate = currentDate.AddDays(1);
            }

            var finalValue = lastDayResult?.Snapshot.PortfolioValue ?? portfolio.TotalValue;
            run.FinalCapital = Math.Round(finalValue, FinancialPrecision);
            run.TotalProfit = Math.Round(finalValue - request.InitialCapital, FinancialPrecision);
            run.TotalReturnPercent = request.InitialCapital > 0m
                ? Math.Round((finalValue - request.InitialCapital) / request.InitialCapital * 100m, FinancialPrecision)
                : 0m;
            run.Status = BacktestRunStatus.Completed;

            await _runRepository.UpdateAsync(run);

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
            run.Status = BacktestRunStatus.Failed;
            await _runRepository.UpdateAsync(run);

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
