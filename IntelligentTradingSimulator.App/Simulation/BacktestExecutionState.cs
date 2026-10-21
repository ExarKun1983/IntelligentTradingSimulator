using System;
using System.Collections.Generic;
using System.Linq;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.App.Simulation;

public sealed record BacktestPendingIntent(
    string Symbol,
    DateTime SignalDate,
    decimal AllocationPercent,
    ExitReason? ExitReason = null);

public sealed class BacktestExecutionState
{
    private readonly Dictionary<string, BacktestPendingIntent> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservationResult> _observations = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, BacktestPendingIntent> Pending => _pending;

    public IReadOnlyList<StockRecommendation> BuildRecommendations(
        IReadOnlyList<ObservationResult> observations,
        PortfolioRecommendationService recommendationService)
    {
        foreach (var observation in observations)
        {
            _observations[observation.Symbol] = observation;
        }

        return recommendationService.Build(_observations.Values.ToList());
    }

    public void ExecuteOpen(
        Portfolio portfolio,
        DateTime date,
        IReadOnlyDictionary<string, decimal> opens,
        PortfolioAllocationEngine allocationEngine,
        TradingEngine tradingEngine)
    {
        var previousHighs = portfolio.Positions.ToDictionary(
            x => x.Symbol, x => x.HighestPriceSinceEntry, StringComparer.OrdinalIgnoreCase);
        var tradesBeforeOpen = portfolio.Trades.Count;
        foreach (var position in portfolio.Positions)
        {
            if (opens.TryGetValue(position.Symbol, out var open))
            {
                // Opening valuation must not advance the close-evaluated trailing high.
                var high = position.HighestPriceSinceEntry;
                position.CurrentPrice = open;
                position.HighestPriceSinceEntry = high;
            }
        }

        var eligible = _pending.Values
            .Where(x => x.SignalDate.Date < date.Date && opens.ContainsKey(x.Symbol))
            .OrderBy(x => x.Symbol, StringComparer.Ordinal)
            .ToList();
        foreach (var intent in eligible)
        {
            // One opening attempt, including an unaffordable or below-threshold target.
            _pending.Remove(intent.Symbol);
        }

        foreach (var intent in eligible.Where(x => x.ExitReason.HasValue || x.AllocationPercent == 0m))
        {
            var position = portfolio.Positions.FirstOrDefault(
                x => string.Equals(x.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase));
            if (position is not null)
            {
                tradingEngine.Execute(portfolio, new TradingDecision
                {
                    Symbol = position.Symbol,
                    Action = TradeActionType.Sell,
                    Amount = position.Quantity * opens[intent.Symbol],
                    ExitReason = intent.ExitReason ?? ExitReason.Rebalance
                }, opens[intent.Symbol], date);
            }
        }

        var targets = eligible
            .Where(x => !x.ExitReason.HasValue && x.AllocationPercent > 0m)
            .Select(x => new StockRecommendation { Symbol = x.Symbol, AllocationPercent = x.AllocationPercent })
            .ToList();
        var instructions = allocationEngine.Calculate(portfolio, targets);
        foreach (var instruction in instructions.Where(x => !x.IsBuy))
        {
            tradingEngine.Execute(portfolio, new TradingDecision
            {
                Symbol = instruction.Symbol,
                Action = TradeActionType.Sell,
                Amount = Math.Abs(instruction.Difference),
                ExitReason = ExitReason.Rebalance
            }, opens[instruction.Symbol], date);
        }

        instructions = allocationEngine.Calculate(portfolio, targets);
        foreach (var trade in allocationEngine.BuildTrades(portfolio, instructions))
        {
            tradingEngine.Execute(portfolio, new TradingDecision
            {
                Symbol = trade.Symbol,
                Action = TradeActionType.Buy,
                Amount = Math.Min(trade.Amount, portfolio.Cash)
            }, opens[trade.Symbol], date);
        }

        var boughtSymbols = portfolio.Trades.Skip(tradesBeforeOpen)
            .Where(x => x.Action == TradeActionType.Buy)
            .Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var position in portfolio.Positions)
        {
            if (previousHighs.TryGetValue(position.Symbol, out var high))
            {
                position.HighestPriceSinceEntry = boughtSymbols.Contains(position.Symbol)
                    ? Math.Max(high, opens[position.Symbol])
                    : high;
            }
        }
    }

    public void GenerateAfterClose(
        Portfolio portfolio,
        DateTime date,
        IReadOnlyDictionary<string, decimal> closes,
        IReadOnlyList<StockRecommendation> recommendations,
        IReadOnlyList<ObservationResult> observations,
        StrategyConfiguration configuration)
    {
        foreach (var position in portfolio.Positions)
        {
            if (position.Quantity <= 0m || !closes.TryGetValue(position.Symbol, out var close))
            {
                continue;
            }

            ExitReason? reason = null;
            if (configuration.StopLossPercent is decimal loss && position.AveragePrice > 0m &&
                close <= position.AveragePrice * (1m - loss / 100m))
            {
                reason = ExitReason.StopLoss;
            }
            else if (configuration.TrailingStopPercent is decimal trailing &&
                position.HighestPriceSinceEntry > 0m &&
                close <= position.HighestPriceSinceEntry * (1m - trailing / 100m))
            {
                reason = ExitReason.TrailingStop;
            }

            if (reason.HasValue &&
                (!_pending.TryGetValue(position.Symbol, out var existing) || !existing.ExitReason.HasValue))
            {
                _pending[position.Symbol] = new(position.Symbol, date.Date, 0m, reason);
            }
        }

        if (recommendations.Count == 0)
        {
            return;
        }

        var analyzed = observations.Select(x => x.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var symbols = recommendations.Select(x => x.Symbol)
            .Concat(portfolio.Positions.Select(x => x.Symbol))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in symbols)
        {
            if (!closes.ContainsKey(symbol) || _pending.ContainsKey(symbol))
            {
                continue;
            }

            var recommendation = recommendations.FirstOrDefault(
                x => string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (recommendation is not null && !analyzed.Contains(symbol))
            {
                continue;
            }

            _pending[symbol] = new(symbol, date.Date, recommendation?.AllocationPercent ?? 0m);
        }
    }
}
