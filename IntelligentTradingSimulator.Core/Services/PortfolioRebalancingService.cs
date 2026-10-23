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
    public IReadOnlyList<StockRecommendation> Execute(
        Portfolio portfolio,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyDictionary<string, decimal> prices,
        PortfolioAllocationEngine allocationEngine,
        TradingEngine tradingEngine,
        decimal? stopLossPercent = null,
        decimal? trailingStopPercent = null,
        DateTime? simulatedTimestamp = null,
        int? maxPositions = null)
    {
        var positionLimit = maxPositions ?? StrategyConfiguration.DefaultMaxPositions;
        if (positionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPositions));
        }

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
                !TradingPrecisionPolicy.TryNormalizePrice(currentPrice, out var normalizedPrice))
            {
                continue;
            }

            currentPrice = normalizedPrice;
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

        ExecuteAllocationSales(recommendations
            .Where(x => x.Score <= 0m && x.AllocationPercent == 0m)
            .ToList());
        recommendations = recommendations
            .Where(x => x.AllocationPercent > 0m || portfolio.Positions.Any(
                position => position.Quantity > 0m &&
                            string.Equals(position.Symbol, x.Symbol, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var heldSymbols = portfolio.Positions
            .Where(x => x.Quantity > 0m)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var topRecommendationSymbols = recommendations
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .Take(positionLimit)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        recommendations = recommendations
            .Where(x => !heldSymbols.Contains(x.Symbol) || topRecommendationSymbols.Contains(x.Symbol))
            .ToList();
        recommendations = new PortfolioRecommendationService(positionLimit)
            .SelectExecutionTargets(recommendations, heldSymbols);
        var effectiveTargets = recommendations
            .Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,
                Score = x.Score,
                AllocationPercent = Math.Min(x.AllocationPercent, allocationEngine.MaxPositionWeight)
            })
            .ToList();

        void ExecuteAllocationSales(IReadOnlyList<StockRecommendation> targets)
        {
            foreach (var instruction in allocationEngine.Calculate(portfolio, targets).Where(x => !x.IsBuy))
            {
                var position = portfolio.Positions.FirstOrDefault(x => x.Symbol == instruction.Symbol);
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
                        Amount = Math.Abs(instruction.Difference)
                    },
                    position.CurrentPrice,
                    simulatedTimestamp);
            }
        }

        ExecuteAllocationSales(effectiveTargets);

        var allocationInstructions =
            allocationEngine.Calculate(
                portfolio,
                effectiveTargets);

        var trades =
            allocationEngine.BuildTrades(
                portfolio,
                allocationInstructions);

        foreach (var trade in trades)
        {
            if (portfolio.Cash <= 0m)
            {
                break;
            }

            var isExistingPosition = portfolio.Positions.Any(
                x => x.Quantity > 0m && string.Equals(x.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase));
            if (!isExistingPosition && portfolio.Positions
                    .Where(x => x.Quantity > 0m)
                    .Select(x => x.Symbol)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= positionLimit)
            {
                continue;
            }

            tradingEngine.Execute(
                portfolio,
                new TradingDecision
                {
                    Symbol = trade.Symbol,
                    Action = TradeActionType.Buy,
                    Amount = Math.Min(trade.Amount, portfolio.Cash)
                },
                prices[trade.Symbol],
                simulatedTimestamp);
        }

        return effectiveTargets;
    }
}