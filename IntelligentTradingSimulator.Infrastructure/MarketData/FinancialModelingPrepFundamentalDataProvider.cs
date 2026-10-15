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

        var growthTask = FetchEndpointAsync(growthUri, cancellationToken);
        var ratiosTask = FetchEndpointAsync(ratiosUri, cancellationToken);

        await Task.WhenAll(growthTask, ratiosTask).ConfigureAwait(false);

        var growthElement = await growthTask.ConfigureAwait(false);
        var ratiosElement = await ratiosTask.ConfigureAwait(false);

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
                ratiosElement.Value, "debtEquityRatio", "debtEquityRatioTTM", "debtToEquity");
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

    private async Task<JsonElement?> FetchEndpointAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"FinancialModelingPrep request to '{uri.GetLeftPart(UriPartial.Path)}' failed with status {(int)response.StatusCode} ({response.StatusCode}): {errorContent}",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.Clone();

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("Error Message", out var errorProp) ||
                root.TryGetProperty("errorMessage", out errorProp) ||
                root.TryGetProperty("error", out errorProp))
            {
                var errorText = errorProp.GetString() ?? "Unknown provider error.";
                throw new InvalidOperationException($"FinancialModelingPrep returned error: {errorText}");
            }

            return root;
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0)
            {
                return null;
            }

            return root[0].Clone();
        }

        return null;
    }

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
