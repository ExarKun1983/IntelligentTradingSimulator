using IntelligentTradingSimulator.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

public sealed class PortfolioAnalyticsService
{
    public decimal CalculateMaxPortfolioValue(
        IReadOnlyList<PortfolioSnapshot> snapshots)
    {
        return snapshots.Max(x => x.PortfolioValue);
    }

    public decimal CalculateMinPortfolioValue(
        IReadOnlyList<PortfolioSnapshot> snapshots)
    {
        return snapshots.Min(x => x.PortfolioValue);
    }

    public decimal CalculateMaxDrawdown(
        IReadOnlyList<PortfolioSnapshot> snapshots)
    {
        decimal peak = 0;
        decimal maxDrawdown = 0;

        foreach (var snapshot in snapshots)
        {
            if (snapshot.PortfolioValue > peak)
            {
                peak = snapshot.PortfolioValue;
            }

            if (peak == 0)
            {
                continue;
            }

            var drawdown =
                (peak - snapshot.PortfolioValue)
                / peak * 100m;

            if (drawdown > maxDrawdown)
            {
                maxDrawdown = drawdown;
            }
        }

        return maxDrawdown;
    }
}