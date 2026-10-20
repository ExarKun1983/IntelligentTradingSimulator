using System.Globalization;
using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using OoplesFinance.YahooFinanceAPI;
using OoplesFinance.YahooFinanceAPI.Enums;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

public sealed class YahooMarketDataProvider
    : IMarketDataProvider
{
    private const string UnavailableMessage = "Requested Information Not Available On Yahoo Finance";
    private const string BadRequestMessage = "Yahoo Finance Server Error BadRequest";
    private static readonly HttpClient ConfirmationClient = new();
    private readonly Func<string, DateTime, DateTime?, Task<IReadOnlyList<MarketCandle>>> _download;
    private readonly HttpClient _confirmationClient;

    public YahooMarketDataProvider()
        : this(DownloadFromYahooAsync)
    {
    }

    internal YahooMarketDataProvider(
        Func<string, DateTime, DateTime?, Task<IReadOnlyList<MarketCandle>>> download,
        HttpClient? confirmationClient = null)
    {
        _download = download;
        _confirmationClient = confirmationClient ?? ConfirmationClient;
    }

    public Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from)
    {
        return GetHistoricalDailyAsync(
            symbol,
            from,
            DateTime.MaxValue.Date);
    }

    public async Task<IReadOnlyList<MarketCandle>>
        GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;

        if (toDate < fromDate)
        {
            return [];
        }

        // Yahoo's period2 is exclusive; the application range is inclusive.
        DateTime? exclusiveEnd = toDate == DateTime.MaxValue.Date
            ? null
            : DateTime.SpecifyKind(toDate.AddDays(1), DateTimeKind.Utc);
        var utcFrom = DateTime.SpecifyKind(fromDate, DateTimeKind.Utc);
        IReadOnlyList<MarketCandle> data;
        try
        {
            data = await _download(symbol, utcFrom, exclusiveEnd)
                ?? throw new InvalidOperationException("Yahoo Finance returned no response.");
        }
        catch (InvalidOperationException ex) when (
            ex.GetType() == typeof(InvalidOperationException) &&
            (ex.Message == UnavailableMessage || ex.Message == BadRequestMessage) &&
            exclusiveEnd.HasValue && !string.IsNullOrWhiteSpace(symbol))
        {
            // Neither the client's unavailable message nor HTTP 400 proves no data.
            // Require a structured, bounded confirmation before certifying coverage.
            if (await ConfirmEmptyRangeAsync(symbol, utcFrom, exclusiveEnd.Value))
                return [];
            throw;
        }

        return data
            .Where(x =>
                x.Date.Date >= fromDate && x.Date.Date <= toDate)
            .OrderBy(x => x.Date)
            .ToList();
    }

    private async Task<bool> ConfirmEmptyRangeAsync(string symbol, DateTime from, DateTime exclusiveEnd)
    {
        var start = new DateTimeOffset(from).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var end = new DateTimeOffset(exclusiveEnd).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var uri = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
            $"?period1={start}&period2={end}&interval=1d";
        using var response = await DownloadConfirmationAsync(uri);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // Yahoo's range-specific 400 body does not identify the symbol, so it alone cannot
            // distinguish a pre-listing range from an unknown ticker. Validate the symbol separately.
            if (await IsRangeNoDataErrorAsync(response, "Bad Request", start, end) &&
                await ConfirmListedAfterAsync(symbol, long.Parse(end, CultureInfo.InvariantCulture)))
                return true;
            response.EnsureSuccessStatusCode();
        }
        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
            return false;
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object ||
            !chart.TryGetProperty("error", out var error) ||
            !chart.TryGetProperty("result", out var result))
            return false;

        if (error.ValueKind == JsonValueKind.Object)
        {
            return response.StatusCode == HttpStatusCode.NotFound &&
                IsRangeNoDataError(result, error, "Not Found", start, end);
        }
        if (response.StatusCode != HttpStatusCode.OK || error.ValueKind != JsonValueKind.Null ||
            result.ValueKind != JsonValueKind.Array || result.GetArrayLength() != 1)
            return false;

        var series = result[0];
        if (series.ValueKind != JsonValueKind.Object ||
            !series.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("symbol", out var returnedSymbol) || returnedSymbol.ValueKind != JsonValueKind.String ||
            !string.Equals(returnedSymbol.GetString(), symbol, StringComparison.OrdinalIgnoreCase) ||
            !series.TryGetProperty("indicators", out var indicators) || indicators.ValueKind != JsonValueKind.Object ||
            !indicators.TryGetProperty("quote", out var quotes) || quotes.ValueKind != JsonValueKind.Array ||
            quotes.GetArrayLength() != 1 || quotes[0].ValueKind != JsonValueKind.Object)
            return false;

        if (series.TryGetProperty("timestamp", out var timestamps) &&
            (timestamps.ValueKind != JsonValueKind.Array || timestamps.GetArrayLength() != 0))
            return false;
        return quotes[0].EnumerateObject().All(x =>
            x.Value.ValueKind == JsonValueKind.Array && x.Value.GetArrayLength() == 0);
    }

    private static async Task<bool> IsRangeNoDataErrorAsync(
        HttpResponseMessage response, string expectedCode, string start, string end)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("chart", out var chart) && chart.ValueKind == JsonValueKind.Object &&
                chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object &&
                chart.TryGetProperty("result", out var result) &&
                IsRangeNoDataError(result, error, expectedCode, start, end);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsRangeNoDataError(
        JsonElement result, JsonElement error, string expectedCode, string start, string end) =>
        result.ValueKind == JsonValueKind.Null &&
        error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String &&
        code.GetString() == expectedCode &&
        error.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String &&
        description.GetString() == $"Data doesn't exist for startDate = {start}, endDate = {end}";

    // Bounded metadata lookup: the symbol must resolve on the chart endpoint and Yahoo's
    // first trade date must be at or after the requested exclusive end (wholly pre-listing).
    private async Task<bool> ConfirmListedAfterAsync(string symbol, long exclusiveEndSeconds)
    {
        var uri = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
            "?range=1d&interval=1d";
        using var response = await DownloadConfirmationAsync(uri);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.OK)
            return false;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object ||
            !chart.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Null ||
            !chart.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array ||
            result.GetArrayLength() != 1 || result[0].ValueKind != JsonValueKind.Object ||
            !result[0].TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("symbol", out var returnedSymbol) || returnedSymbol.ValueKind != JsonValueKind.String ||
            !string.Equals(returnedSymbol.GetString(), symbol, StringComparison.OrdinalIgnoreCase) ||
            !meta.TryGetProperty("firstTradeDate", out var firstTradeDate) ||
            firstTradeDate.ValueKind != JsonValueKind.Number ||
            !firstTradeDate.TryGetInt64(out var firstTradeSeconds))
            return false;
        return firstTradeSeconds >= exclusiveEndSeconds;
    }

    private async Task<HttpResponseMessage> DownloadConfirmationAsync(string uri)
    {
        var response = await SendConfirmationRequestAsync(uri);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();
        using var sessionResponse = await SendConfirmationRequestAsync("https://fc.yahoo.com");
        var cookies = sessionResponse.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders)
            ? string.Join("; ", setCookieHeaders
                .Select(x => x.Split(';', 2)[0])
                .Where(x => !string.IsNullOrWhiteSpace(x)))
            : "";
        if (string.IsNullOrWhiteSpace(cookies))
            throw new InvalidOperationException("Yahoo Finance did not provide a session cookie.");

        using var crumbResponse = await SendConfirmationRequestAsync(
            "https://query1.finance.yahoo.com/v1/test/getcrumb", cookies);
        crumbResponse.EnsureSuccessStatusCode();
        var crumb = await crumbResponse.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(crumb))
            throw new InvalidOperationException("Yahoo Finance returned no session crumb.");
        return await SendConfirmationRequestAsync(uri + "&crumb=" + Uri.EscapeDataString(crumb), cookies);
    }

    private async Task<HttpResponseMessage> SendConfirmationRequestAsync(string uri, string? cookies = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36");
        if (!string.IsNullOrWhiteSpace(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        return await _confirmationClient.SendAsync(request);
    }

    private static async Task<IReadOnlyList<MarketCandle>> DownloadFromYahooAsync(
        string symbol, DateTime from, DateTime? to)
    {
        var yahooClient = new YahooClient();
        var data = await yahooClient.GetHistoricalDataAsync(symbol, DataFrequency.Daily, from, to);
        return data.Select(x => new MarketCandle
        {
            Date = x.Date,
            Open = (decimal)x.Open,
            High = (decimal)x.High,
            Low = (decimal)x.Low,
            Close = (decimal)x.Close,
            Volume = (long)x.Volume
        }).ToList();
    }
}