using System.Globalization;
using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.Extensions.Options;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

/// <summary>
/// External fundamental data provider integrating FinancialModelingPrep (FMP).
/// Loads Revenue Growth, EPS Growth, Margins, and Debt for a given stock symbol.
/// Maps external units to the provider-neutral <see cref="FundamentalData"/> model.
/// </summary>
public sealed class FinancialModelingPrepFundamentalDataProvider : IFundamentalDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly FinancialModelingPrepOptions _options;

    public FinancialModelingPrepFundamentalDataProvider(
        HttpClient httpClient,
        IOptions<FinancialModelingPrepOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<FundamentalData?> GetFundamentalDataAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "FinancialModelingPrep API key is not configured. " +
                "Configure 'FundamentalData:FinancialModelingPrep:ApiKey' in application settings.");
        }

        var baseUri = new Uri(
            string.IsNullOrWhiteSpace(_options.BaseUrl)
                ? FinancialModelingPrepOptions.DefaultBaseUrl
                : _options.BaseUrl.TrimEnd('/') + "/");

        var cleanSymbol = symbol.Trim().ToUpperInvariant();
        var encodedSymbol = Uri.EscapeDataString(cleanSymbol);
        var encodedApiKey = Uri.EscapeDataString(_options.ApiKey.Trim());

        var growthUri = new Uri(
            baseUri,
            $"stable/financial-growth?symbol={encodedSymbol}&limit=1&apikey={encodedApiKey}");

        var ratiosUri = new Uri(
            baseUri,
            $"stable/ratios?symbol={encodedSymbol}&limit=1&apikey={encodedApiKey}");

        var growthTask = FetchEndpointAsync(growthUri, FundamentalDataEndpoint.FinancialGrowth, cancellationToken);
        var ratiosTask = FetchEndpointAsync(ratiosUri, FundamentalDataEndpoint.Ratios, cancellationToken);

        var endpoints = await Task.WhenAll(growthTask, ratiosTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var failures = endpoints.Where(result => result.Failure is not null)
            .Select(result => result.Failure!).ToArray();
        if (failures.Length > 0)
        {
            throw new FundamentalDataFetchException(failures);
        }

        var growthElement = endpoints[0].Data;
        var ratiosElement = endpoints[1].Data;

        if (!growthElement.HasValue && !ratiosElement.HasValue)
        {
            return null;
        }

        var growthDate = GetDate(growthElement);
        var ratiosDate = GetDate(ratiosElement);
        var includeGrowth = growthElement.HasValue;
        var includeRatios = ratiosElement.HasValue;
        DateOnly? asOfDate;

        if (growthElement.HasValue && ratiosElement.HasValue)
        {
            if (growthDate.HasValue && ratiosDate.HasValue)
            {
                if (growthDate != ratiosDate)
                {
                    includeGrowth = growthDate > ratiosDate;
                    includeRatios = ratiosDate > growthDate;
                }

                asOfDate = growthDate > ratiosDate ? growthDate : ratiosDate;
            }
            else if (growthDate.HasValue)
            {
                includeRatios = false;
                asOfDate = growthDate;
            }
            else if (ratiosDate.HasValue)
            {
                includeGrowth = false;
                asOfDate = ratiosDate;
            }
            else
            {
                return null;
            }
        }
        else
        {
            asOfDate = growthDate ?? ratiosDate;
        }

        decimal? revenueGrowthPercent = null;
        decimal? epsGrowthPercent = null;
        if (includeGrowth && growthElement.HasValue)
        {
            var rawRevGrowth = GetDecimalProperty(
                growthElement.Value, "revenueGrowth", "revenueGrowthTTM");
            if (rawRevGrowth.HasValue)
            {
                revenueGrowthPercent = rawRevGrowth.Value * 100m;
            }

            var rawEpsGrowth = GetDecimalProperty(
                growthElement.Value, "epsgrowth", "epsGrowth", "epsGrowthTTM", "epsdilutedGrowth");
            if (rawEpsGrowth.HasValue)
            {
                epsGrowthPercent = rawEpsGrowth.Value * 100m;
            }
        }

        decimal? margins = null;
        decimal? debt = null;
        if (includeRatios && ratiosElement.HasValue)
        {
            var rawMargin = GetDecimalProperty(
                ratiosElement.Value, "netProfitMargin", "netProfitMarginTTM");
            if (rawMargin.HasValue)
            {
                margins = rawMargin.Value * 100m;
            }

            var rawDebt = GetDecimalProperty(
                ratiosElement.Value, "debtToEquityRatio", "debtEquityRatio", "debtEquityRatioTTM", "debtToEquity");
            if (rawDebt.HasValue)
            {
                debt = rawDebt.Value;
            }
        }

        return new FundamentalData
        {
            Symbol = cleanSymbol,
            AsOfDate = asOfDate,
            RevenueGrowthPercent = revenueGrowthPercent,
            EpsGrowthPercent = epsGrowthPercent,
            Margins = margins,
            Debt = debt
        };
    }

    private sealed record EndpointResult(JsonElement? Data = null, FundamentalDataFailure? Failure = null);

    private async Task<EndpointResult> FetchEndpointAsync(
        Uri uri,
        FundamentalDataEndpoint endpoint,
        CancellationToken cancellationToken)
    {
        int? status = null;
        EndpointResult Fail(FundamentalDataFailureCategory category) =>
            new(Failure: new FundamentalDataFailure(endpoint, category, status));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new();
            }

            if (!response.IsSuccessStatusCode)
            {
                return Fail(ClassifyStatus(status));
            }

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return Fail(FundamentalDataFailureCategory.InvalidJson);
            }
            if (string.IsNullOrWhiteSpace(json))
            {
                return new();
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    return new();
                }

                root = root[0].Clone();
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("Error Message", out _) ||
                    root.TryGetProperty("errorMessage", out _) ||
                    root.TryGetProperty("error", out _))
                {
                    return Fail(FundamentalDataFailureCategory.ProviderError);
                }

                return new(root);
            }

            return root.ValueKind == JsonValueKind.Null
                ? new()
                : Fail(FundamentalDataFailureCategory.InvalidJson);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Fundamental data fetch canceled by caller.", cancellationToken);
            }

            return Fail(FundamentalDataFailureCategory.Timeout);
        }
        catch (HttpRequestException exception)
        {
            status ??= (int?)exception.StatusCode;
            return Fail(status >= 400 ? ClassifyStatus(status) : FundamentalDataFailureCategory.Transport);
        }
        catch (IOException)
        {
            return Fail(FundamentalDataFailureCategory.Transport);
        }
        catch (JsonException)
        {
            return Fail(FundamentalDataFailureCategory.InvalidJson);
        }
    }

    private static FundamentalDataFailureCategory ClassifyStatus(int? status) => status switch
    {
        401 or 403 => FundamentalDataFailureCategory.AuthAccess,
        429 => FundamentalDataFailureCategory.RateLimit,
        >= 500 and <= 599 => FundamentalDataFailureCategory.UpstreamServer,
        _ => FundamentalDataFailureCategory.HttpFailure
    };

    private static decimal? GetDecimalProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop))
            {
                if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDecimal(out var value))
                {
                    return value;
                }

                if (prop.ValueKind == JsonValueKind.String &&
                    decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string? GetStringProperty(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                return prop.GetString();
            }
        }

        return null;
    }

    private static DateOnly? GetDate(JsonElement? element)
    {
        if (!element.HasValue)
        {
            return null;
        }

        var dateStr = GetStringProperty(element.Value, "date", "fillingDate");
        return DateOnly.TryParse(dateStr, CultureInfo.InvariantCulture, out var date)
            ? date
            : null;
    }
}
