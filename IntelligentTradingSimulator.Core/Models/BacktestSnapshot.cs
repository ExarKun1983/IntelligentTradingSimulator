using System;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestSnapshot
{
    public int Id { get; set; }

    public int BacktestRunId { get; set; }

    public BacktestRun BacktestRun { get; set; }
        = null!;

    public DateTime Date { get; set; }

    public decimal PortfolioValue { get; set; }

    public decimal Cash { get; set; }

    public decimal ProfitLoss { get; set; }

    public decimal ReturnPercent { get; set; }
}
