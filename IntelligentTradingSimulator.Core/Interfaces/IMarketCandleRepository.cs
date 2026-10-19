using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IMarketCandleRepository
{
    Task<IReadOnlyList<MarketCandle>> GetRangeAsync(
        string symbol, string source, string priceAdjustmentMode, DateTime from, DateTime to);

    Task<IReadOnlyList<MarketDataCoverage>> GetCoverageAsync(
        string symbol, string source, string priceAdjustmentMode, DateTime from, DateTime to);

    Task UpsertAsync(
        string symbol, string source, string priceAdjustmentMode,
        DateTime from, DateTime to, IReadOnlyList<MarketCandle> candles);
}
