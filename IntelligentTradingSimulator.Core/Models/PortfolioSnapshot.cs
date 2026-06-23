using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class PortfolioSnapshot
{
    public int Id { get; set; }

    public int PortfolioId { get; set; }

    public DateTime Timestamp { get; set; }

    public decimal PortfolioValue { get; set; }

    public decimal Cash { get; set; }

    public decimal ProfitLoss { get; set; }

    public decimal ReturnPercent { get; set; }
}
