namespace IntelligentTradingSimulator.Infrastructure.MarketData;

/// <summary>
/// Configuration settings for the FinancialModelingPrep fundamental data provider.
/// </summary>
public sealed class FinancialModelingPrepOptions
{
    public const string SectionName = "FundamentalData:FinancialModelingPrep";
    public const string DefaultBaseUrl = "https://financialmodelingprep.com/";
    public const int DefaultRequestTimeoutSeconds = 10;
    public const int MaximumRequestTimeoutSeconds = 30;

    /// <summary>
    /// API key for authenticating with FinancialModelingPrep.
    /// Never commit real keys or secrets into source control.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Base URL for the FinancialModelingPrep API endpoints.
    /// </summary>
    public string BaseUrl { get; set; } = DefaultBaseUrl;

    /// <summary>
    /// Maximum duration of an individual FinancialModelingPrep HTTP request.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = DefaultRequestTimeoutSeconds;
}
