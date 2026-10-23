using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioExecutionService
{
    private readonly decimal _maxPositionWeight;
    private readonly int _maxPositions;

    public PortfolioExecutionService(
        decimal maxPositionWeight = 100m,
        int maxPositions = StrategyConfiguration.DefaultMaxPositions)
    {
        if (maxPositionWeight <= 0m ||
            maxPositionWeight > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPositionWeight),
                maxPositionWeight,
                "Max position weight must be greater than zero and less than or equal to 100.");
        }

        if (maxPositions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPositions));
        }

        _maxPositionWeight =
            maxPositionWeight;
        _maxPositions = maxPositions;
    }

    public void ExecuteRecommendations(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices,
        TradingEngine tradingEngine)
    {
        var capitalToInvest =
            portfolio.StartingCash * 0.80m;

        foreach (var recommendation in recommendations)
        {
            var isExistingPosition = portfolio.Positions.Any(
                x => x.Quantity > 0m &&
                     string.Equals(x.Symbol, recommendation.Symbol, StringComparison.OrdinalIgnoreCase));
            var openPositionCount = portfolio.Positions
                .Where(x => x.Quantity > 0m)
                .Select(x => x.Symbol)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (!isExistingPosition && openPositionCount >= _maxPositions)
            {
                continue;
            }

            var allocationPercent =
                Math.Min(
                    recommendation.AllocationPercent,
                    _maxPositionWeight);

            var amount =
                capitalToInvest
                * allocationPercent
                / 100m;

            var decision =
                new TradingDecision
                {
                    Symbol = recommendation.Symbol,
                    Action = TradeActionType.Buy,
                    Amount = amount
                };

            tradingEngine.Execute(
                portfolio,
                decision,
                prices[recommendation.Symbol]);
        }
    }
}