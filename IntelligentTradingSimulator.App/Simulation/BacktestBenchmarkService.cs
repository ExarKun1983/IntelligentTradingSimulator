using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestBenchmarkService : IBacktestBenchmarkService
{
    private readonly IBacktestRunRepository _runRepository;
    private readonly IBenchmarkDataService _benchmarkDataService;
    private readonly IWorkflowProgress _progress;

    public BacktestBenchmarkService(
        IBacktestRunRepository runRepository,
        IBenchmarkDataService benchmarkDataService,
        IWorkflowProgress? progress = null)
    {
        _runRepository = runRepository;
        _benchmarkDataService = benchmarkDataService;
        _progress = progress ?? NullWorkflowProgress.Instance;
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
        BacktestRun? run;
        using (_progress.BeginOperation("Loading benchmark backtest run"))
            run = await _runRepository.GetByIdAsync(backtestRunId);

        if (run is null)
        {
            _progress.Report($"Benchmark report failed: backtest run {backtestRunId} not found.", force: true);
            return new BacktestBenchmarkResult
            {
                IsSuccess = false,
                ErrorMessage = $"Backtest run {backtestRunId} not found."
            };
        }

        IReadOnlyList<BenchmarkResult> benchmarkResults;
        using (_progress.BeginOperation("Calculating backtest benchmarks"))
            benchmarkResults = await _benchmarkDataService.GetBenchmarkResultsAsync(
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
