using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestBenchmarkService : IBacktestBenchmarkService
{
    private readonly IBacktestRunRepository _runRepository;
    private readonly IBenchmarkDataService _benchmarkDataService;

    public BacktestBenchmarkService(
        IBacktestRunRepository runRepository,
        IBenchmarkDataService benchmarkDataService)
    {
        _runRepository = runRepository;
        _benchmarkDataService = benchmarkDataService;
    }

    public async Task<BacktestBenchmarkResult> GetForRunAsync(
        int backtestRunId)
    {
        return await GetForRunAsync(backtestRunId, BenchmarkUniverse.DefaultSymbols);
    }

    public async Task<BacktestBenchmarkResult> GetForRunAsync(
        int backtestRunId,
        IEnumerable<string> symbols)
    {
        var run = await _runRepository.GetByIdAsync(backtestRunId);

        if (run is null)
        {
            return new BacktestBenchmarkResult
            {
                IsSuccess = false,
                ErrorMessage = $"Backtest run {backtestRunId} not found."
            };
        }

        var benchmarkResults =
            await _benchmarkDataService.GetBenchmarkResultsAsync(
                symbols,
                run.StartDate,
                run.EndDate);

        return new BacktestBenchmarkResult
        {
            IsSuccess = true,
            BacktestRunId = run.Id,
            BenchmarkResults = benchmarkResults
        };
    }
}
