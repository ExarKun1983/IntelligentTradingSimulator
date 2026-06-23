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
        PortfolioAllocationService allocationService)
    {
        _allocationService = allocationService;
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
            var difference =
                _allocationService
                    .CalculateDifference(
                        portfolio,
                        recommendation);

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
