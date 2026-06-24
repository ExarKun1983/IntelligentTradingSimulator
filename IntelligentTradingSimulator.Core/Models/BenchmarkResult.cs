namespace IntelligentTradingSimulator.Core.Models;

public sealed class BenchmarkResult
{
    public string Symbol { get; set; } = string.Empty;

    public decimal StartPrice { get; set; }

    public decimal EndPrice { get; set; }

    public decimal ReturnPercent { get; set; }
}
