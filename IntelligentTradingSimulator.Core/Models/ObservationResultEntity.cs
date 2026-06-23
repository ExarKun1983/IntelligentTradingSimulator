namespace IntelligentTradingSimulator.Core.Models;

public sealed class ObservationResultEntity
{
    public int Id { get; set; }

    public int ObservationRunId { get; set; }

    public ObservationRun ObservationRun { get; set; }
        = null!;

    public string Symbol { get; set; } = string.Empty;

    public decimal MomentumPercent { get; set; }

    public decimal VolumeTrendPercent { get; set; }

    public decimal VolatilityPercent { get; set; }

    public bool IsAboveSma5 { get; set; }

    public decimal Score { get; set; }

    public int RankPosition { get; set; }
}