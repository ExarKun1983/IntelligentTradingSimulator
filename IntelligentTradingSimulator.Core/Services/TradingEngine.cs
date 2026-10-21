using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class TradingEngine
{
    public void Execute(
    Portfolio portfolio,
    TradingDecision decision,
    decimal currentPrice,
    DateTime? simulatedTimestamp = null)
    {
        if (decision.Action == TradeActionType.Buy)
        {
            ExecuteBuy(
                portfolio,
                decision,
                currentPrice,
                simulatedTimestamp);

            return;
        }

        if (decision.Action == TradeActionType.Sell)
        {
            ExecuteSell(
                portfolio,
                decision,
                currentPrice,
                simulatedTimestamp);

            return;
        }

        if (decision.Action == TradeActionType.Hold)
        {
            return;
        }
    }

    private void ExecuteBuy(
        Portfolio portfolio,
        TradingDecision decision,
        decimal currentPrice,
        DateTime? simulatedTimestamp)
    {
        if (portfolio.Cash < 0m
            || !TradingPrecisionPolicy.TryNormalizePrice(currentPrice, out var price)
            || !TradingPrecisionPolicy.TryNormalizeCash(decision.Amount, out var allocation))
        {
            return;
        }

        decimal quantity;
        decimal notional;
        try
        {
            if (!TradingPrecisionPolicy.TryNormalizeQuantity(allocation / price, out quantity)
                || !TradingPrecisionPolicy.TryNormalizeNotional(quantity * price, out notional))
            {
                return;
            }

            if (notional > portfolio.Cash)
            {
                if (allocation - portfolio.Cash > price * 0.00000001m)
                {
                    return;
                }

                quantity = TradingPrecisionPolicy.FloorQuantity(portfolio.Cash / price);
                if (quantity <= 0m
                    || !TradingPrecisionPolicy.TryNormalizeNotional(quantity * price, out notional)
                    || notional > portfolio.Cash)
                {
                    return;
                }
            }
        }
        catch (OverflowException)
        {
            return;
        }

        var existingPosition =
            portfolio.Positions.FirstOrDefault(
                x => x.Symbol == decision.Symbol);

        decimal newQuantity;
        decimal averagePrice;
        try
        {
            if (existingPosition is not null
                && (existingPosition.Quantity <= 0m
                    || existingPosition.AveragePrice < 0m
                    || !TradingPrecisionPolicy.PriceIsInRange(existingPosition.AveragePrice)))
            {
                return;
            }

            if (!TradingPrecisionPolicy.TryNormalizeQuantity(
                existingPosition is null ? quantity : existingPosition.Quantity + quantity,
                out newQuantity))
            {
                return;
            }

            var previousCost = existingPosition is null
                ? 0m
                : existingPosition.Quantity * existingPosition.AveragePrice;
            averagePrice = TradingPrecisionPolicy.RoundAveragePrice(
                (previousCost + quantity * price) / newQuantity);

            if (newQuantity <= 0m || averagePrice <= 0m)
            {
                return;
            }
        }
        catch (OverflowException)
        {
            return;
        }

        var newCash = Math.Round(portfolio.Cash - notional,
            TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero);
        if (newCash < 0m || !TradingPrecisionPolicy.CashIsInRange(newCash))
        {
            return;
        }

        portfolio.Cash = newCash;

        if (existingPosition is null)
        {
            portfolio.Positions.Add(
                new Position
                {
                    Symbol = decision.Symbol,
                    Quantity = newQuantity,
                    AveragePrice = averagePrice,
                    CurrentPrice = price
                });
        }
        else
        {
            existingPosition.Quantity = newQuantity;
            existingPosition.AveragePrice = averagePrice;
            existingPosition.CurrentPrice = price;
        }

        portfolio.Trades.Add(
            new Trade
            {
                Timestamp = simulatedTimestamp ?? DateTime.UtcNow,
                Symbol = decision.Symbol,
                Action = TradeActionType.Buy,
                Quantity = quantity,
                Price = price
            });
    }

    private void ExecuteSell(
        Portfolio portfolio,
        TradingDecision decision,
        decimal currentPrice,
        DateTime? simulatedTimestamp)
    {
        var hasNormalizedAllocation =
            TradingPrecisionPolicy.TryNormalizeCash(decision.Amount, out var allocation);
        if (!TradingPrecisionPolicy.TryNormalizePrice(currentPrice, out var price)
            || decision.Amount <= 0m
            || (!hasNormalizedAllocation
                && Math.Round(decision.Amount, TradingPrecisionPolicy.CashScale,
                    MidpointRounding.AwayFromZero) != 0m))
        {
            return;
        }

        var position =
            portfolio.Positions.FirstOrDefault(
                x => x.Symbol == decision.Symbol);

        if (position is null || position.Quantity <= 0m)
        {
            return;
        }

        decimal quantityToSell;
        decimal receivedCash;
        try
        {
            var requestedQuantity = allocation / price;
            var rawFullLiquidationNotional = position.Quantity * price;
            var hasRoundedFullLiquidationNotional = TradingPrecisionPolicy.TryNormalizeNotional(
                rawFullLiquidationNotional, out var roundedFullLiquidationNotional);
            var isFullLiquidation = requestedQuantity >= position.Quantity
                || decision.Amount >= rawFullLiquidationNotional
                || (hasRoundedFullLiquidationNotional && allocation >= roundedFullLiquidationNotional);
            quantityToSell = isFullLiquidation
                ? position.Quantity
                : TradingPrecisionPolicy.FloorQuantity(requestedQuantity);

            if (quantityToSell <= 0m
                || quantityToSell > position.Quantity)
            {
                return;
            }

            receivedCash = Math.Round(quantityToSell * price,
                TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero);
            if (receivedCash < 0m
                || !TradingPrecisionPolicy.CashIsInRange(receivedCash)
                || (receivedCash == 0m && !isFullLiquidation))
            {
                return;
            }
        }
        catch (OverflowException)
        {
            return;
        }

        decimal newCash;
        try
        {
            newCash = Math.Round(portfolio.Cash + receivedCash,
                TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return;
        }

        if (!TradingPrecisionPolicy.CashIsInRange(newCash))
        {
            return;
        }

        portfolio.Cash = newCash;
        if (quantityToSell == position.Quantity)
        {
            portfolio.Positions.Remove(position);
        }
        else
        {
            position.Quantity -= quantityToSell;
            position.CurrentPrice = price;
        }

        portfolio.Trades.Add(
            new Trade
            {
                Timestamp = simulatedTimestamp ?? DateTime.UtcNow,
                Symbol = decision.Symbol,
                Action = TradeActionType.Sell,
                ExitReason = decision.ExitReason ?? ExitReason.Manual,
                Quantity = quantityToSell,
                Price = price
            });
    }
}