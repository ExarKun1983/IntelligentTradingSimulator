using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioAllocationService
{
    public bool AllowsAllocationSale(
        Portfolio portfolio,
        StockRecommendation target,
        decimal maxPositionWeight = 100m)
    {
        var position = portfolio.Positions.FirstOrDefault(x =>
            string.Equals(x.Symbol, target.Symbol, StringComparison.OrdinalIgnoreCase));
        return target.Score <= 0m && target.AllocationPercent == 0m ||
               target.AllocationPercent > 0m &&
               position?.EffectiveTargetPercent is decimal baseline &&
               Math.Min(target.AllocationPercent, maxPositionWeight) < Math.Min(baseline, maxPositionWeight);
    }

    public decimal CalculatePortfolioValue(
        Portfolio portfolio)
    {
        return portfolio.Cash +
               portfolio.Positions.Sum(
                   x => x.Quantity * x.CurrentPrice);
    }

    public decimal CalculateTargetValue(
        Portfolio portfolio,
        StockRecommendation recommendation)
    {
        var totalValue =
            CalculatePortfolioValue(
                portfolio);

        return totalValue *
               recommendation.AllocationPercent
               / 100m;
    }

    public decimal CalculateDifference(
    Portfolio portfolio,
    StockRecommendation recommendation)
    {
        var targetValue =
            CalculateTargetValue(
                portfolio,
                recommendation);

        var position =
            portfolio.Positions
                .FirstOrDefault(
                    x => x.Symbol ==
                         recommendation.Symbol);

        var currentValue = 0m;

        if (position is not null)
        {
            currentValue =
                position.Quantity *
                position.CurrentPrice;
        }

        return targetValue - currentValue;
    }
}