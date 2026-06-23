using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioContributionService
{
    public IReadOnlyList<PositionContribution>
        Calculate(
            Portfolio portfolio)
    {
        var result =
            new List<PositionContribution>();

        foreach (var position in portfolio.Positions)
        {
            var cost =
                position.Quantity *
                position.AveragePrice;

            var value =
                position.Quantity *
                position.CurrentPrice;

            var profitLoss =
                value - cost;

            var returnPercent =
                cost == 0
                ? 0
                : profitLoss / cost * 100m;

            result.Add(
                new PositionContribution
                {
                    Symbol =
                        position.Symbol,

                    ProfitLoss =
                        profitLoss,

                    ReturnPercent =
                        returnPercent
                });
        }

        return result
            .OrderByDescending(
                x => x.ProfitLoss)
            .ToList();
    }
}