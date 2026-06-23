using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Services;

public sealed class PortfolioPerformanceService
{
    public PortfolioPerformance Calculate(
        Portfolio portfolio)
    {
        var positionsValue =
            portfolio.Positions.Sum(
                x => x.Quantity * x.CurrentPrice);

        var portfolioValue =
            portfolio.Cash + positionsValue;

        var profitLoss =
            portfolioValue - portfolio.StartingCash;

        var returnPercent =
            profitLoss /
            portfolio.StartingCash * 100m;

        return new PortfolioPerformance
        {
            PortfolioValue = portfolioValue,
            ProfitLoss = profitLoss,
            ReturnPercent = returnPercent
        };
    }
}
