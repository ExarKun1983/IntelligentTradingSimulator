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
    public void ExecuteRecommendations(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations,
        Dictionary<string, decimal> prices,
        TradingEngine tradingEngine)
    {
        foreach (var recommendation in recommendations)
        {
            var capitalToInvest = portfolio.StartingCash * 0.80m;

            var amount =
                capitalToInvest
                * recommendation.AllocationPercent
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