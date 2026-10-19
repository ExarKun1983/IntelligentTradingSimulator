namespace IntelligentTradingSimulator.Core.Models;

public sealed class MarketDataCoverage
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Source { get; set; } = "";
    public string PriceAdjustmentMode { get; set; } = "";
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public DateTime DownloadedAt { get; set; }
}
