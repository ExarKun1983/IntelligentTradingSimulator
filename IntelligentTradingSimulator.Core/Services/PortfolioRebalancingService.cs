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
        TradingEngine tradingEngine,
        decimal? stopLossPercent = null,
        decimal? trailingStopPercent = null,
        DateTime? simulatedTimestamp = null)
    {
        if (stopLossPercent is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(stopLossPercent));
        }

        if (trailingStopPercent is <= 0m or > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(trailingStopPercent));
        }

        var stoppedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var position in portfolio.Positions.ToList())
        {
            if (position.Quantity <= 0m ||
                !prices.TryGetValue(position.Symbol, out var currentPrice) ||
                currentPrice <= 0m)
            {
                continue;
            }

            position.HighestPriceSinceEntry = Math.Max(position.HighestPriceSinceEntry, currentPrice);
            ExitReason? exitReason = null;
            if (stopLossPercent is decimal lossPercent && position.AveragePrice > 0m &&
                currentPrice <= position.AveragePrice * (1m - lossPercent / 100m))
            {
                exitReason = ExitReason.StopLoss;
            }
            else if (trailingStopPercent is decimal drawdownPercent &&
                position.HighestPriceSinceEntry > 0m &&
                currentPrice <= position.HighestPriceSinceEntry * (1m - drawdownPercent / 100m))
            {
                exitReason = ExitReason.TrailingStop;
            }

            if (exitReason is null)
            {
                continue;
            }

            tradingEngine.Execute(
                portfolio,
                new TradingDecision
                {
                    Symbol = position.Symbol,
                    Action = TradeActionType.Sell,
                    ExitReason = exitReason,
                    Amount = position.Quantity * currentPrice
                },
                currentPrice,
                simulatedTimestamp);
            stoppedSymbols.Add(position.Symbol);
        }

        recommendations = recommendations
            .Where(x => !stoppedSymbols.Contains(x.Symbol))
            .ToList();

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
                position.CurrentPrice,
                simulatedTimestamp);
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
                prices[trade.Symbol],
                simulatedTimestamp);
        }
    }
}