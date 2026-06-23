using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Core.Models;

public sealed class ObservationResult
{
    public string Symbol { get; set; } = string.Empty;

    public decimal MomentumPercent { get; set; }

    public decimal AverageVolume { get; set; }

    public decimal VolumeTrendPercent { get; set; }

    public decimal VolatilityPercent { get; set; }

    public bool IsAboveSma5 { get; set; }

    public decimal Score { get; set; }
}
