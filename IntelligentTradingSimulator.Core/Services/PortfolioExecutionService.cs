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

    public PortfolioExecutionService(
        decimal maxPositionWeight = 100m)
    {
        if (maxPositionWeight <= 0m ||
            maxPositionWeight > 100m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPositionWeight),
                maxPositionWeight,
                "Max position weight must be greater than zero and less than or equal to 100.");
        }

        _maxPositionWeight =
            maxPositionWeight;
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