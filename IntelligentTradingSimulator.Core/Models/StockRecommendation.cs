using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class StockRecommendation
{
    public string Symbol { get; set; } = string.Empty;

    public decimal AllocationPercent { get; set; }

    public decimal Score { get; set; }
}