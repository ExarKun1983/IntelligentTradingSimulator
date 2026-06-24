using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IBacktestBenchmarkService
{
    Task<BacktestBenchmarkResult> GetForRunAsync(int backtestRunId);

    Task<BacktestBenchmarkResult> GetForRunAsync(
        int backtestRunId,
        IEnumerable<string> symbols);
}
