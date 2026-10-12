using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioRebalancingService
{
    public void Execute(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices,
        PortfolioAllocationEngine allocationEngine,
        TradingEngine tradingEngine)
    {
        var allocationInstructions =
            allocationEngine.Calculate(
                portfolio,
                recommendations);

        var sellInstructions =
            allocationInstructions
                .Where(x => !x.IsBuy)
                .ToList();

        foreach (var instruction in sellInstructions)
        {
            var position =
                portfolio.Positions
                    .FirstOrDefault(
                        x => x.Symbol ==
                             instruction.Symbol);

            if (position is null)
            {
                continue;
            }

            tradingEngine.Execute(
                portfolio,
                new TradingDecision
                {
                    Symbol = instruction.Symbol,
                    Action = TradeActionType.Sell,
                    ExitReason = ExitReason.Rebalance,
                    Amount = Math.Abs(
                        instruction.Difference)
                },
                position.CurrentPrice);
        }

        allocationInstructions =
            allocationEngine.Calculate(
                portfolio,
                recommendations);

        var trades =
            allocationEngine.BuildTrades(
                portfolio,
                allocationInstructions);

        foreach (var trade in trades)
        {
            tradingEngine.Execute(
                portfolio,
                new TradingDecision
                {
                    Symbol = trade.Symbol,
                    Action = TradeActionType.Buy,
                    Amount = trade.Amount
                },
                prices[trade.Symbol]);
        }
    }
}