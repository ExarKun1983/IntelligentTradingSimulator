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
    ExitReason? ExitReason = null,
    decimal Score = 0m,
    bool AllowAllocationSale = false,
    decimal? BaselineAllocationPercent = null);

public sealed class BacktestExecutionState
{
    private readonly Dictionary<string, BacktestPendingIntent> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObservationResult> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StockRecommendation> _lastSelectedTargets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _diagnostics = [];

    public IReadOnlyDictionary<string, BacktestPendingIntent> Pending => _pending;
    public IReadOnlyList<string> Diagnostics => _diagnostics;

    public void ClearDiagnostics() => _diagnostics.Clear();

    public IReadOnlyList<StockRecommendation> BuildRecommendations(
        IReadOnlyList<ObservationResult> observations,
        IReadOnlyList<string> symbols,
        PortfolioRecommendationService recommendationService)
    {
        var universe = symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in _observations.Keys.Where(x => !universe.Contains(x)).ToList())
        {
            _observations.Remove(symbol);
        }
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
        TradingEngine tradingEngine,
        int maxPositions = StrategyConfiguration.DefaultMaxPositions)
    {
        if (maxPositions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPositions));
        }

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

        foreach (var intent in eligible.Where(x => x.ExitReason.HasValue))
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

        var targetIntents = eligible
            .Where(x => !x.ExitReason.HasValue)
            .Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,
                AllocationPercent = x.AllocationPercent,
                Score = x.Score
            })
            .Where(x => x.Score <= 0m || x.AllocationPercent > 0m)
            .ToList();
        var saleTargets = eligible
            .Where(x => !x.ExitReason.HasValue &&
                        (x.AllowAllocationSale ||
                         x.BaselineAllocationPercent is null &&
                         IsBelowCurrentAllocation(x, portfolio)) &&
                        (x.Score <= 0m || x.AllocationPercent > 0m))
            .Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,
                AllocationPercent = x.AllocationPercent,
                Score = x.Score
            })
            .ToList();
        var instructions = allocationEngine.Calculate(portfolio, saleTargets);
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

        var heldSymbols = portfolio.Positions
            .Where(x => x.Quantity > 0m)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var availableSlots = Math.Max(0, maxPositions - heldSymbols.Count);
        var targets = new PortfolioRecommendationService(maxPositions)
            .SelectExecutionTargets(
                targetIntents.Where(x => x.AllocationPercent > 0m).ToList(),
                heldSymbols, availableSlots, reweightAllocations: false);
        var selectedNewTargets = targets
            .Where(x => !heldSymbols.Contains(x.Symbol))
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var blocked in targetIntents.Where(x =>
                     x.AllocationPercent > 0m &&
                     !heldSymbols.Contains(x.Symbol) && !selectedNewTargets.Contains(x.Symbol)))
        {
            _diagnostics.Add(
                $"Purchase blocked for {blocked.Symbol} on {date:yyyy-MM-dd}: the {maxPositions}-position limit is full.");
        }

        instructions = allocationEngine.Calculate(portfolio, targets);
        foreach (var trade in allocationEngine.BuildTrades(portfolio, instructions))
        {
            var isExistingPosition = portfolio.Positions.Any(
                x => x.Quantity > 0m &&
                     string.Equals(x.Symbol, trade.Symbol, StringComparison.OrdinalIgnoreCase));
            if (!isExistingPosition && CountOpenPositions(portfolio) >= maxPositions)
            {
                _diagnostics.Add(
                    $"Purchase blocked for {trade.Symbol} on {date:yyyy-MM-dd}: the {maxPositions}-position limit is full.");
                continue;
            }

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
        if (configuration.MaxPositions <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration.MaxPositions));
        }

        var observationsBySymbol = observations
            .GroupBy(x => x.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var observedSymbols = observationsBySymbol.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            else if (configuration.UseHoldingConditionExit &&
                     observationsBySymbol.TryGetValue(position.Symbol, out var observation) &&
                     observation.MomentumPercent <= 0m)
            {
                reason = ExitReason.HoldingCondition;
            }

            if (reason.HasValue &&
                (!_pending.TryGetValue(position.Symbol, out var existing) || !existing.ExitReason.HasValue))
            {
                _pending[position.Symbol] = new(position.Symbol, date.Date, 0m, reason);
                var detail = reason == ExitReason.HoldingCondition &&
                             observationsBySymbol.TryGetValue(position.Symbol, out var holdingObservation)
                    ? $"momentum {holdingObservation.MomentumPercent}% <= 0%"
                    : reason.Value.ToString();
                _diagnostics.Add(
                    $"Exit signaled for {position.Symbol} on {date:yyyy-MM-dd}: {reason} ({detail}).");
            }
        }

        var heldSymbols = portfolio.Positions
            .Where(x => x.Quantity > 0m)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var exitingSymbols = _pending.Values
            .Where(x => x.ExitReason.HasValue && heldSymbols.Contains(x.Symbol))
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentTargets = recommendations
            .Where(x => closes.ContainsKey(x.Symbol))
            .Where(x => !_pending.TryGetValue(x.Symbol, out var existing) || !existing.ExitReason.HasValue)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Symbol, StringComparer.Ordinal)
            .ToList();
        foreach (var pendingBuy in _pending.Values
                     .Where(x => !x.ExitReason.HasValue &&
                                 observedSymbols.Contains(x.Symbol) &&
                                 !currentTargets.Any(target => string.Equals(
                                     target.Symbol, x.Symbol, StringComparison.OrdinalIgnoreCase)))
                     .ToList())
        {
            _pending.Remove(pendingBuy.Symbol);
            _diagnostics.Add(
                $"Purchase intent for {pendingBuy.Symbol} was rejected on {date:yyyy-MM-dd}: it no longer qualifies for an available position slot.");
        }

        foreach (var target in currentTargets)
        {
            if (_pending.TryGetValue(target.Symbol, out var pending) &&
                !pending.ExitReason.HasValue && pending.Score != target.Score)
            {
                _pending[target.Symbol] = pending with { Score = target.Score, SignalDate = date.Date };
            }
        }

        var activePendingBuys = _pending.Values
            .Where(x => !x.ExitReason.HasValue)
            .ToList();
        var deferredPendingBuys = activePendingBuys.Count(x => !heldSymbols.Contains(x.Symbol));
        var availableSlots = Math.Max(
            0,
            configuration.MaxPositions - heldSymbols.Count + exitingSymbols.Count - deferredPendingBuys);
        var activePendingSymbols = activePendingBuys
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var freshTargets = currentTargets
            .Where(x => !activePendingSymbols.Contains(x.Symbol))
            .ToList();
        var candidates = freshTargets.Where(x => !heldSymbols.Contains(x.Symbol)).ToList();
        var selectedCandidates = new PortfolioRecommendationService(configuration.MaxPositions)
            .SelectExecutionTargets(freshTargets, heldSymbols, availableSlots, reweightAllocations: false);
        var staleHeldTargets = _lastSelectedTargets.Values
            .Where(x => heldSymbols.Contains(x.Symbol) &&
                        !currentTargets.Any(target => string.Equals(
                            target.Symbol, x.Symbol, StringComparison.OrdinalIgnoreCase)) &&
                        !_pending.ContainsKey(x.Symbol))
            .ToList();

        foreach (var blocked in candidates.Skip(availableSlots))
        {
            _diagnostics.Add(
                $"Purchase blocked for {blocked.Symbol} on {date:yyyy-MM-dd}: the {configuration.MaxPositions}-position limit leaves no available slot.");
        }

        var remainingPendingBuys = _pending.Values
            .Where(x => !x.ExitReason.HasValue)
            .ToList();
        var allocationPlan = selectedCandidates
            .Concat(staleHeldTargets)
            .Concat(remainingPendingBuys.Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,
                Score = x.Score,
                AllocationPercent = x.AllocationPercent
            }))
            .ToList();
        var planScore = allocationPlan.Sum(x => x.Score);
        var normalizedPlan = allocationPlan
            .Select(x => new StockRecommendation
            {
                Symbol = x.Symbol,
                Score = x.Score,
                AllocationPercent = planScore > 0m
                    ? Math.Round(100m * x.Score / planScore, 2)
                    : x.AllocationPercent
            })
            .ToList();
        var normalizedBySymbol = normalizedPlan.ToDictionary(
            x => x.Symbol, StringComparer.OrdinalIgnoreCase);
        foreach (var pending in remainingPendingBuys)
        {
            if (normalizedBySymbol.TryGetValue(pending.Symbol, out var target))
            {
                var baseline = pending.BaselineAllocationPercent;
                var allocationChanged = pending.AllocationPercent != target.AllocationPercent;
                if (baseline is null && allocationChanged && heldSymbols.Contains(pending.Symbol))
                {
                    baseline = GetAllocationBaseline(pending.Symbol, portfolio, closes);
                }

                _lastSelectedTargets[pending.Symbol] = target;
                var updated = pending with
                {
                    SignalDate = allocationChanged ? date.Date : pending.SignalDate,
                    AllocationPercent = target.AllocationPercent,
                    AllowAllocationSale = allocationChanged
                        ? IsIntentionalAllocationReduction(target, baseline)
                        : pending.AllowAllocationSale,
                    BaselineAllocationPercent = baseline
                };
                if (updated != pending)
                {
                    _pending[pending.Symbol] = updated;
                }
            }
        }
        foreach (var retained in staleHeldTargets)
        {
            if (normalizedBySymbol.TryGetValue(retained.Symbol, out var target))
            {
                _lastSelectedTargets[retained.Symbol] = target;
            }
        }
        var allocatedCandidates = selectedCandidates
            .Select(x => normalizedBySymbol[x.Symbol])
            .ToList();

        foreach (var recommendation in allocatedCandidates)
        {
            var symbol = recommendation.Symbol;
            if (_pending.TryGetValue(symbol, out var existing) && existing.ExitReason.HasValue)
            {
                _diagnostics.Add(
                    $"Purchase intent for {symbol} was rejected on {date:yyyy-MM-dd}: an exit signal is active.");
                continue;
            }

            if (_pending.ContainsKey(symbol))
            {
                continue;
            }

            var baseline = GetAllocationBaseline(symbol, portfolio, closes);
            _pending[symbol] = new(
                symbol,
                date.Date,
                recommendation.AllocationPercent,
                Score: recommendation.Score,
                AllowAllocationSale: IsIntentionalAllocationReduction(recommendation, baseline),
                BaselineAllocationPercent: baseline);
            _lastSelectedTargets[symbol] = recommendation;
        }
    }

    private static bool IsIntentionalAllocationReduction(
        StockRecommendation target,
        decimal? baselineAllocationPercent) =>
        target.Score <= 0m && target.AllocationPercent == 0m ||
        baselineAllocationPercent is decimal baseline &&
        target.AllocationPercent < baseline;

    private decimal? GetAllocationBaseline(
        string symbol,
        Portfolio portfolio,
        IReadOnlyDictionary<string, decimal> closes)
    {
        if (_lastSelectedTargets.TryGetValue(symbol, out var previousTarget))
        {
            return previousTarget.AllocationPercent;
        }

        var position = portfolio.Positions.FirstOrDefault(x =>
            string.Equals(x.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (position is null || position.Quantity <= 0m)
        {
            return null;
        }

        var totalValue = portfolio.Cash + portfolio.Positions.Sum(x =>
            x.Quantity * (closes.TryGetValue(x.Symbol, out var close) && close > 0m
                ? close
                : x.CurrentPrice));
        if (totalValue <= 0m)
        {
            return null;
        }

        var currentPrice = closes.TryGetValue(position.Symbol, out var positionClose) && positionClose > 0m
            ? positionClose
            : position.CurrentPrice;
        return Math.Round(position.Quantity * currentPrice / totalValue * 100m, 2);
    }

    private static bool IsBelowCurrentAllocation(
        BacktestPendingIntent intent,
        Portfolio portfolio)
    {
        var position = portfolio.Positions.FirstOrDefault(x =>
            string.Equals(x.Symbol, intent.Symbol, StringComparison.OrdinalIgnoreCase));
        var totalValue = portfolio.Cash +
                         portfolio.Positions.Sum(x => x.Quantity * x.CurrentPrice);
        return position is not null &&
               position.Quantity > 0m &&
               totalValue > 0m &&
               intent.AllocationPercent <
               Math.Round(position.Quantity * position.CurrentPrice / totalValue * 100m, 2);
    }

    private static int CountOpenPositions(Portfolio portfolio) =>
        portfolio.Positions
            .Where(x => x.Quantity > 0m)
            .Select(x => x.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

}
