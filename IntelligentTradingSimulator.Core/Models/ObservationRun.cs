namespace IntelligentTradingSimulator.Core.Models;

public sealed class ObservationRun
{
    public int Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<ObservationResultEntity> Results { get; set; }
        = new();
}