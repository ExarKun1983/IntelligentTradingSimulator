using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed class BacktestEngineResult
{
    public int RunId { get; set; }

    public BacktestRunStatus Status { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal TotalProfit { get; set; }

    public decimal TotalReturnPercent { get; set; }

    public string? ErrorMessage { get; set; }
}
