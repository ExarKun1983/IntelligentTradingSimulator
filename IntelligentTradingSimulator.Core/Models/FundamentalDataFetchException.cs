namespace IntelligentTradingSimulator.Core.Models;

public enum FundamentalDataEndpoint
{
    FinancialGrowth,
    Ratios
}

public enum FundamentalDataFailureCategory
{
    AuthAccess,
    RateLimit,
    UpstreamServer,
    HttpFailure,
    Transport,
    Timeout,
    InvalidJson,
    ProviderError
}

public sealed record FundamentalDataFailure(
    FundamentalDataEndpoint Endpoint,
    FundamentalDataFailureCategory Category,
    int? HttpStatus)
{
    public string Provider => "FinancialModelingPrep";

    public override string ToString() =>
        $"Provider={Provider}; Endpoint={Endpoint switch
        {
            FundamentalDataEndpoint.FinancialGrowth => "financial-growth",
            FundamentalDataEndpoint.Ratios => "ratios",
            _ => "unknown"
        }}; HTTP={HttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; Category={Category}";
}

/// <summary>Contains only allowlisted diagnostics, never upstream exceptions or response text.</summary>
public sealed class FundamentalDataFetchException : Exception
{
    public FundamentalDataFetchException(IEnumerable<FundamentalDataFailure> failures)
        : base("Fundamental data fetch failed; see safe endpoint diagnostics.")
    {
        Failures = Array.AsReadOnly(failures.ToArray());
    }

    public IReadOnlyList<FundamentalDataFailure> Failures { get; }
}
