using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record PortfolioState(
    Portfolio Portfolio,
    PortfolioPerformance Performance,
    decimal TotalValue)
{
    public PaperExecutionOutcome Outcome { get; init; } = PaperExecutionOutcome.Executed;
    public IReadOnlyList<StockRecommendation> ExecutionTargets { get; init; } = [];
}
