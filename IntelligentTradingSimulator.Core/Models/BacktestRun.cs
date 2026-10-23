using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class BacktestRun
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string StrategyId { get; set; } = string.Empty;

    public string? StrategyConfigurationJson { get; set; }

    public DateTime StartDate { get; set; }

    public DateTime EndDate { get; set; }

    public decimal InitialCapital { get; set; }

    public decimal FinalCapital { get; set; }

    public decimal TotalProfit { get; set; }

    public decimal TotalReturnPercent { get; set; }

    public BacktestRunStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public int? PortfolioId { get; set; }

    public Portfolio? Portfolio { get; set; }

    public List<BacktestSnapshot> Snapshots { get; set; }
        = new();
}
