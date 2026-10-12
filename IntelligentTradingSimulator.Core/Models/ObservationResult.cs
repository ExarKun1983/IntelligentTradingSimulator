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

    public decimal? Rsi { get; set; }

    public decimal? Atr { get; set; }

    public decimal? Week52High { get; set; }

    public decimal? DistanceFromWeek52HighPercent { get; set; }

    public string? RelativeStrengthBenchmarkSymbol { get; set; }

    public decimal? RelativeStrengthPercent { get; set; }

    public decimal? RelativeStrengthScoreContribution { get; set; }

    public IReadOnlyList<FactorScoreContribution>? FactorContributions { get; set; }

    public decimal Score { get; set; }
}
