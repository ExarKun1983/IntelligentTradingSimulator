using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioAllocationEngine
{
    private readonly PortfolioAllocationService
        _allocationService;

    private readonly decimal _maxPositionWeight;

    public decimal MaxPositionWeight => _maxPositionWeight;

    public IReadOnlyList<AllocationTrade>
    BuildTrades(
        Portfolio portfolio,
        IReadOnlyList<AllocationInstruction> instructions)
    {
        var result =
            new List<AllocationTrade>();

        var availableCash =
            portfolio.Cash;

        foreach (var instruction in
                 instructions.Where(x => x.IsBuy))
        {
            if (availableCash <= 0)
            {
                break;
            }

            var amount =
                Math.Min(
                    availableCash,
                    instruction.Difference);

            result.Add(
                new AllocationTrade
                {
                    Symbol =
                        instruction.Symbol,

                    Amount =
                        amount
                });

            availableCash -= amount;
        }

        return result;
    }

    public PortfolioAllocationEngine(
        PortfolioAllocationService allocationService,
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

        _allocationService = allocationService;
        _maxPositionWeight = maxPositionWeight;
    }

    public IReadOnlyList<AllocationInstruction>
        Calculate(
            Portfolio portfolio,
            IReadOnlyList<StockRecommendation> recommendations)
    {
        var result =
            new List<AllocationInstruction>();

        foreach (var recommendation in recommendations)
        {
            var cappedPercent =
                Math.Min(
                    recommendation.AllocationPercent,
                    _maxPositionWeight);

            var cappedRecommendation =
                new StockRecommendation
                {
                    Symbol = recommendation.Symbol,
                    AllocationPercent = cappedPercent,
                    Score = recommendation.Score
                };

            var difference =
                _allocationService
                    .CalculateDifference(
                        portfolio,
                        cappedRecommendation);

            if (Math.Abs(difference) < 100m)
            {
                continue;
            }

            result.Add(
                new AllocationInstruction
                {
                    Symbol =
                        recommendation.Symbol,

                    Difference =
                        difference,

                    IsBuy =
                        difference > 0
                });
        }

        return result
            .OrderByDescending(
                x => Math.Abs(x.Difference))
            .ToList();
    }
}
