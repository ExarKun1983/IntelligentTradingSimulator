using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class PositionSnapshot
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public DateTime Timestamp { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal CurrentPrice { get; set; }

    public decimal PositionValue { get; set; }

    public decimal Weight { get; set; }
}