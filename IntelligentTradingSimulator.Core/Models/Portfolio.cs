using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class Portfolio
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public decimal StartingCash { get; set; }

    public decimal Cash { get; set; }

    public List<Position> Positions { get; set; } = new();

    public List<Trade> Trades { get; set; } = new();

    public decimal PositionsValue =>
    Positions.Sum(x => x.MarketValue);

    public decimal TotalValue =>
        Cash + PositionsValue;
}