using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class PortfolioPerformance
{
    public decimal PortfolioValue { get; set; }

    public decimal ProfitLoss { get; set; }

    public decimal ReturnPercent { get; set; }
}