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
    decimal currentPrice)
    {
        if (decision.Action == TradeActionType.Buy)
        {
            ExecuteBuy(
                portfolio,
                decision,
                currentPrice);

            return;
        }

        if (decision.Action == TradeActionType.Sell)
        {
            ExecuteSell(
                portfolio,
                decision,
                currentPrice);

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
        decimal currentPrice)
    {
        if (portfolio.Cash < decision.Amount)
        {
            return;
        }

        var quantity =
            decision.Amount / currentPrice;

        quantity = Math.Round(
            quantity,
            4,
            MidpointRounding.AwayFromZero);

        if (quantity <= 0)
        {
            return;
        }

        portfolio.Cash -= decision.Amount;

        var existingPosition =
            portfolio.Positions.FirstOrDefault(
                x => x.Symbol == decision.Symbol);

        if (existingPosition is null)
        {
            portfolio.Positions.Add(
                new Position
                {
                    Symbol = decision.Symbol,
                    Quantity = quantity,
                    AveragePrice = currentPrice,
                    CurrentPrice = currentPrice
                });
        }
        else
        {
            var totalCost =
                existingPosition.Quantity *
                existingPosition.AveragePrice;

            var newCost =
                quantity * currentPrice;

            existingPosition.Quantity += quantity;

            existingPosition.AveragePrice =
                (totalCost + newCost)
                / existingPosition.Quantity;

            existingPosition.CurrentPrice =
                currentPrice;
        }

        portfolio.Trades.Add(
            new Trade
            {
                Timestamp = DateTime.UtcNow,
                Symbol = decision.Symbol,
                Action = TradeActionType.Buy,
                Quantity = quantity,
                Price = currentPrice
            });
    }

    private void ExecuteSell(
        Portfolio portfolio,
        TradingDecision decision,
        decimal currentPrice)
    {
        var position =
            portfolio.Positions.FirstOrDefault(
                x => x.Symbol == decision.Symbol);

        if (position is null)
        {
            return;
        }

        var quantityToSell =
            decision.Amount / currentPrice;

        if (quantityToSell > position.Quantity)
        {
            quantityToSell = position.Quantity;
        }

        var receivedCash =
            quantityToSell * currentPrice;

        portfolio.Cash += receivedCash;

        position.Quantity -= quantityToSell;

        position.CurrentPrice = currentPrice;

        portfolio.Trades.Add(
            new Trade
            {
                Timestamp = DateTime.UtcNow,
                Symbol = decision.Symbol,
                Action = TradeActionType.Sell,
                Quantity = quantityToSell,
                Price = currentPrice
            });

        if (position.Quantity <= 0)
        {
            portfolio.Positions.Remove(position);
        }
    }
}