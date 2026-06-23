using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class RankChange
{
    public string Symbol { get; set; } = string.Empty;

    public int PreviousRank { get; set; }

    public int CurrentRank { get; set; }

    public int Change =>
        PreviousRank - CurrentRank;
}
