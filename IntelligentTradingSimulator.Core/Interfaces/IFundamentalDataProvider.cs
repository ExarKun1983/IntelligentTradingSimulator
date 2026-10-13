using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IFundamentalDataProvider
{
    /// <summary>
    /// Loads a snapshot for a symbol, or null when no snapshot exists.
    /// Missing metrics remain null; loading errors are propagated, not treated as missing data.
    /// Implementations map their source data and units to the provider-neutral model.
    /// </summary>
    Task<FundamentalData?> GetFundamentalDataAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
