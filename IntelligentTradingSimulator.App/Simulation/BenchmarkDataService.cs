using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BenchmarkDataService : IBenchmarkDataService
{
    private const int FinancialPrecision = 4;

    private readonly BacktestHistoricalMarketDataService _marketDataService;
    private readonly IWorkflowProgress _progress;

    public BenchmarkDataService(
        BacktestHistoricalMarketDataService marketDataService,
        IWorkflowProgress? progress = null)
    {
        _marketDataService = marketDataService;
        _progress = progress ?? NullWorkflowProgress.Instance;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> GetBenchmarkResultsAsync(
        IEnumerable<string> symbols,
        DateTime from,
        DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        var results = new List<BenchmarkResult>();
        var missingSymbols = 0;

        foreach (var symbol in symbols)
        {
            using var operation = _progress.BeginOperation("Calculating benchmark", symbol);
            var candles =
                await _marketDataService.GetHistoricalDailyRangeAsync(
                    symbol,
                    fromDate,
                    toDate);

            if (candles.Count == 0)
            {
                missingSymbols++;
                continue;
            }

            var startPrice = candles[0].Close;
            var endPrice = candles[^1].Close;

            var returnPercent = startPrice > 0m
                ? Math.Round(
                    (endPrice - startPrice) / startPrice * 100m,
                    FinancialPrecision)
                : 0m;

            results.Add(new BenchmarkResult
            {
                Symbol = symbol,
                StartPrice = startPrice,
                EndPrice = endPrice,
                ReturnPercent = returnPercent
            });
        }

        if (missingSymbols > 0)
            _progress.Report($"Benchmark warning: no candles for {missingSymbols} symbol(s).", force: true);

        return results;
    }
}
