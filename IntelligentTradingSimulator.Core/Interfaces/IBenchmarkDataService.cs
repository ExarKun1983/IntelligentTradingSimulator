using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IBenchmarkDataService
{
    Task<IReadOnlyList<BenchmarkResult>> GetBenchmarkResultsAsync(
        IEnumerable<string> symbols,
        DateTime from,
        DateTime to);
}
