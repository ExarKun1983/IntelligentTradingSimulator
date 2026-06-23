using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class AllocationInstruction
{
    public string Symbol { get; set; } = string.Empty;

    public decimal Difference { get; set; }

    public bool IsBuy { get; set; }
}
