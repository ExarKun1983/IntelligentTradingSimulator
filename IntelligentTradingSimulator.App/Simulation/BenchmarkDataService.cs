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

    public BenchmarkDataService(
        BacktestHistoricalMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<IReadOnlyList<BenchmarkResult>> GetBenchmarkResultsAsync(
        IEnumerable<string> symbols,
        DateTime from,
        DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        var results = new List<BenchmarkResult>();

        foreach (var symbol in symbols)
        {
            var candles =
                await _marketDataService.GetHistoricalDailyRangeAsync(
                    symbol,
                    fromDate,
                    toDate);

            if (candles.Count == 0)
            {
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

        return results;
    }
}
