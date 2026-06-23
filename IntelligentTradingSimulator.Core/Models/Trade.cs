using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class Trade
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public DateTime Timestamp { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public TradeActionType Action { get; set; }

    public decimal Quantity { get; set; }

    public decimal Price { get; set; }
}
