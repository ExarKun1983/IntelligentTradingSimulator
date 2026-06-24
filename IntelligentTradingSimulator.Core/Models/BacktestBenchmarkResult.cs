using System.Collections.Generic;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestBenchmarkResult
{
    public bool IsSuccess { get; set; }

    public string? ErrorMessage { get; set; }

    public int BacktestRunId { get; set; }

    public IReadOnlyList<BenchmarkResult> BenchmarkResults { get; set; } = [];
}
