using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class Position
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal AveragePrice { get; set; }
    public decimal CurrentPrice { get; set; }

    public decimal MarketValue =>
    Quantity * CurrentPrice;

    public decimal InvestedValue =>
        Quantity * AveragePrice;

    public decimal ProfitLoss =>
        MarketValue - InvestedValue;
}
