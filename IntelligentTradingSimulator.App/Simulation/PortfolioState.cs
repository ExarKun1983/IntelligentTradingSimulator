using System.Collections.Generic;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record PortfolioState(
    Portfolio Portfolio,
    PortfolioPerformance Performance,
    decimal TotalValue)
{
    public IReadOnlyList<StockRecommendation> ExecutionTargets { get; init; } = [];
}
