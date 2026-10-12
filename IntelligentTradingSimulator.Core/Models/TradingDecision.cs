using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.Core.Models;
public sealed class TradingDecision
{
    public string Symbol { get; set; } = string.Empty;
    public TradeActionType Action { get; set; }
    public decimal Amount { get; set; }
    public ExitReason? ExitReason { get; set; }
}
