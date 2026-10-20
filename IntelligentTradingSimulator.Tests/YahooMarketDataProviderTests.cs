using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.MarketData;

namespace IntelligentTradingSimulator.Tests;

public sealed class YahooMarketDataProviderTests
{
    private const string UnavailableMessage = "Requested Information Not Available On Yahoo Finance";
    private const string BadRequestMessage = "Yahoo Finance Server Error BadRequest";
    private static readonly DateTime Saturday = new(2020, 1, 11);
    private static readonly DateTime Sunday = new(2020, 1, 12);

    [Fact]
    public async Task GetHistoricalDailyAsync_ForwardsInclusiveRangeAsUtcAndExclusiveYahooEnd()
    {
        var from = new DateTime(2024, 1, 2);
        var to = new DateTime(2024, 1, 5);
        string? requestedSymbol = null;
        DateTime requestedFrom = default;
        DateTime? requestedTo = null;
        var provider = new YahooMarketDataProvider((symbol, upstreamFrom, upstreamTo) =>
        {
            requestedSymbol = symbol;
            requestedFrom = upstreamFrom;
            requestedTo = upstreamTo;
            return Task.FromResult<IReadOnlyList<MarketCandle>>([]);
        });

        await provider.GetHistoricalDailyAsync("AAPL", from, to);

        Assert.Equal("AAPL", requestedSymbol);
        Assert.Equal(DateTimeKind.Utc, requestedFrom.Kind);
        Assert.Equal(DateTimeKind.Utc, requestedTo!.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(from, DateTimeKind.Utc), requestedFrom);
        Assert.Equal(DateTime.SpecifyKind(to.AddDays(1), DateTimeKind.Utc), requestedTo);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"timestamp\":[]")]
    public async Task GetHistoricalDailyAsync_ConfirmsEmptyChartUsingSameBoundedRange(string timestamp)
    {
        var json = "{\"chart\":{\"error\":null,\"result\":[{\"meta\":{\"symbol\":\"SPY\"}," +
            "\"indicators\":{\"quote\":[{}]}" + timestamp + "}]}}";
        var handler = new ConfirmationHandler(HttpStatusCode.OK, json);
        using var client = new HttpClient(handler);
        var provider = UnavailableProvider(client);

        Assert.Empty(await provider.GetHistoricalDailyAsync("SPY", Saturday, Sunday));

        Assert.Equal("/v8/finance/chart/SPY", handler.Request!.AbsolutePath);
        Assert.Equal("?period1=1578700800&period2=1578873600&interval=1d", handler.Request.Query);
        Assert.Equal(1, handler.RequestCount);
        Assert.Contains("Mozilla/5.0", handler.UserAgent);
    }

    [Fact]
    public async Task GetHistoricalDailyAsync_ConfirmationCanAuthenticateAndRetryIdenticalRangeOnce()
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.OK,
            """{"chart":{"error":null,"result":[{"meta":{"symbol":"AAPL"},"indicators":{"quote":[{}]}}]}}""")
        {
            RequireCrumb = true
        };
        using var client = new HttpClient(handler);

        Assert.Empty(await UnavailableProvider(client).GetHistoricalDailyAsync("AAPL", Saturday, Sunday));

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(1, handler.SessionRequestCount);
        Assert.Equal(1, handler.CrumbRequestCount);
        Assert.Equal("A1=test-session", handler.CrumbCookie);
        Assert.Equal(handler.CrumbCookie, handler.ChartCookie);
        Assert.Equal("?period1=1578700800&period2=1578873600&interval=1d&crumb=test%2Fsession", handler.Request!.Query);
    }

    [Theory]
    [InlineData(401, "")]
    [InlineData(429, "rate limited")]
    [InlineData(500, "server error")]
    [InlineData(200, "")]
    public async Task GetHistoricalDailyAsync_FailedConfirmationAuthenticationRemainsRetryable(int status, string crumb)
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.OK, "{}")
        {
            RequireCrumb = true, CrumbStatus = (HttpStatusCode)status, Crumb = crumb
        };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            UnavailableProvider(client).GetHistoricalDailyAsync("AAPL", Saturday, Sunday));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, handler.SessionRequestCount);
        Assert.Equal(1, handler.CrumbRequestCount);
    }

    [Theory]
    [InlineData("PLTR")]
    [InlineData("SNOW")]
    public async Task GetHistoricalDailyAsync_ConfirmsRangeSpecificPreListingResponse(string symbol)
    {
        using var client = new HttpClient(new ConfirmationHandler(HttpStatusCode.NotFound,
            """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1578700800, endDate = 1578873600"}}}"""));

        Assert.Empty(await UnavailableProvider(client).GetHistoricalDailyAsync(symbol, Saturday, Sunday));
    }

    [Theory]
    [InlineData(404, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"PLTR"},"indicators":{"quote":[{}]}}],"error":null}}""")]
    public async Task GetHistoricalDailyAsync_ConfirmsPltrPreListingBadRequestOnlyAfterStructuredConfirmation(
        int status, string json)
    {
        using var handler = new ConfirmationHandler((HttpStatusCode)status, json);
        using var client = new HttpClient(handler);
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException(BadRequestMessage), client);

        Assert.Empty(await provider.GetHistoricalDailyAsync("PLTR", new DateTime(2019, 5, 10), new DateTime(2019, 5, 14)));

        Assert.Equal("/v8/finance/chart/PLTR", handler.Request!.AbsolutePath);
        Assert.Equal("?period1=1557446400&period2=1557878400&interval=1d", handler.Request.Query);
        Assert.Equal(1, handler.RequestCount);
    }

    // Exact body returned by Yahoo for PLTR, May 10-14, 2019 (period2 exclusive), captured 2026-10-20.
    private const string PltrPreListingBadRequest =
        """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""";
    private const string PltrMetadata =
        """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":1601472600},"indicators":{"quote":[{}]}}],"error":null}}""";
    private static readonly DateTime PltrFrom = new(2019, 5, 10);
    private static readonly DateTime PltrTo = new(2019, 5, 14);

    [Theory]
    [InlineData("PLTR")]
    [InlineData("pltr")]
    public async Task GetHistoricalDailyAsync_ConfirmsActualPltrBadRequestAfterSymbolValidation(string symbol)
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.BadRequest, PltrPreListingBadRequest)
        {
            MetadataStatus = HttpStatusCode.OK, MetadataJson = PltrMetadata
        };
        using var client = new HttpClient(handler);
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException(BadRequestMessage), client);

        Assert.Empty(await provider.GetHistoricalDailyAsync(symbol, PltrFrom, PltrTo));

        Assert.Equal($"/v8/finance/chart/{symbol}", handler.Request!.AbsolutePath);
        Assert.Equal("?period1=1557446400&period2=1557878400&interval=1d", handler.Request.Query);
        Assert.Equal($"/v8/finance/chart/{symbol}", handler.MetadataRequest!.AbsolutePath);
        Assert.Equal("?range=1d&interval=1d", handler.MetadataRequest.Query);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(1, handler.MetadataRequestCount);
    }

    [Fact]
    public async Task GetHistoricalDailyAsync_ExactBadRequestMetadataAuthenticationFailureRemainsFailure()
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.BadRequest, PltrPreListingBadRequest)
        {
            MetadataStatus = HttpStatusCode.Unauthorized, MetadataJson = "{}"
        };
        using var client = new HttpClient(handler);
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException(BadRequestMessage), client);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetHistoricalDailyAsync("PLTR", PltrFrom, PltrTo));

        Assert.Equal(2, handler.MetadataRequestCount);
        Assert.Equal(1, handler.SessionRequestCount);
        Assert.Equal(1, handler.CrumbRequestCount);
    }

    public static TheoryData<int, string, int, string> UnconfirmedBadRequestCases => new()
    {
        // Mismatched dates, code, description, result, and malformed/unknown shapes never reach symbol validation.
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557964800"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557360000, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"bad request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400."}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Invalid input: Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Invalid input"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":[],"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}""", 200, PltrMetadata },
        { 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":null}}}""", 200, PltrMetadata },
        { 400, """{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}""", 200, PltrMetadata },
        { 400, "Data doesn't exist for startDate = 1557446400, endDate = 1557878400", 200, PltrMetadata },
        { 400, "", 200, PltrMetadata },
        { 400, "[]", 200, PltrMetadata },
    };

    public static TheoryData<int, string, int, string> UnvalidatedSymbolCases => new()
    {
        // Exact range-specific 400, but the symbol cannot be positively validated as listed after the range.
        { 400, PltrPreListingBadRequest, 404, """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""" },
        { 400, PltrPreListingBadRequest, 400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Invalid input"}}}""" },
        { 400, PltrPreListingBadRequest, 429, "{}" },
        { 400, PltrPreListingBadRequest, 500, "{}" },
        { 400, PltrPreListingBadRequest, 200, "not json" },
        { 400, PltrPreListingBadRequest, 200, "{}" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":null,"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":1601472600}}],"error":{"code":"Unknown"}}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"OTHER","firstTradeDate":1601472600}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"firstTradeDate":1601472600}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR"}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":null}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":"1601472600"}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":1601472600}},{"meta":{"symbol":"PLTR","firstTradeDate":1601472600}}],"error":null}}""" },
        // Listed before or during the requested range: the 400 is not a pre-listing result.
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":1557878399}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":1557446400}}],"error":null}}""" },
        { 400, PltrPreListingBadRequest, 200, """{"chart":{"result":[{"meta":{"symbol":"PLTR","firstTradeDate":345479400}}],"error":null}}""" },
    };

    [Theory]
    [MemberData(nameof(UnconfirmedBadRequestCases))]
    [MemberData(nameof(UnvalidatedSymbolCases))]
    public async Task GetHistoricalDailyAsync_DoesNotConfirmUnvalidatedPltrBadRequest(
        int status, string json, int metadataStatus, string metadataJson)
    {
        foreach (var message in new[] { UnavailableMessage, BadRequestMessage })
        {
            using var handler = new ConfirmationHandler((HttpStatusCode)status, json)
            {
                MetadataStatus = (HttpStatusCode)metadataStatus, MetadataJson = metadataJson
            };
            using var client = new HttpClient(handler);
            var provider = new YahooMarketDataProvider(
                (_, _, _) => throw new InvalidOperationException(message), client);

            await Assert.ThrowsAnyAsync<Exception>(() => provider.GetHistoricalDailyAsync("PLTR", PltrFrom, PltrTo));
            Assert.Equal(1, handler.RequestCount);
            Assert.True(handler.MetadataRequestCount <= 1);
        }
    }

    [Fact]
    public async Task GetHistoricalDailyAsync_UnknownTickerExactBadRequestRemainsFailure()
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.BadRequest, PltrPreListingBadRequest);
        using var client = new HttpClient(handler);
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException(BadRequestMessage), client);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.GetHistoricalDailyAsync("ZZZXQ", PltrFrom, PltrTo));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Equal("/v8/finance/chart/ZZZXQ", handler.MetadataRequest!.AbsolutePath);
        Assert.Equal(1, handler.MetadataRequestCount);
    }

    [Fact]
    public async Task GetHistoricalDailyAsync_ExactBadRequestMetadataNetworkFailureRemainsFailure()
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.BadRequest, PltrPreListingBadRequest)
        {
            MetadataStatus = HttpStatusCode.OK, MetadataJson = PltrMetadata
        };
        using var client = new HttpClient(new MetadataFailureHandler(handler));
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException(BadRequestMessage), client);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetHistoricalDailyAsync("PLTR", PltrFrom, PltrTo));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("PLTR")]
    public async Task GetHistoricalDailyAsync_DoesNotConfirmBadRequestForInvalidSymbolOrUnboundedRange(string symbol)
    {
        using var handler = new ConfirmationHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        var expected = new InvalidOperationException(BadRequestMessage);
        var provider = new YahooMarketDataProvider((_, _, _) => throw expected, client);

        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetHistoricalDailyAsync(symbol, Saturday, symbol == "PLTR" ? DateTime.MaxValue.Date : Sunday)));
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Invalid input"}}}""")]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1578700800, endDate = 1578873600"}}}""")]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(429, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1578700800, endDate = 1578873600"}}}""")]
    [InlineData(500, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1578700800, endDate = 1578873600"}}}""")]
    [InlineData(503, "{}")]
    [InlineData(404, "<html>Not Found</html>")]
    [InlineData(404, """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""")]
    [InlineData(404, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 0, endDate = 1"}}}""")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(200, """{"chart":{"result":[],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":null,"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"OTHER"},"indicators":{"quote":[{}]}}],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"AAPL"}}],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"AAPL"},"timestamp":null,"indicators":{"quote":[{}]}}],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"AAPL"},"timestamp":[1578672000],"indicators":{"quote":[{}]}}],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"AAPL"},"indicators":{"quote":[{"close":[100]}]}}],"error":null}}""")]
    [InlineData(200, """{"chart":{"result":[{"meta":{"symbol":"AAPL"},"indicators":{"quote":[{}]}}],"error":{"code":"Unknown"}}}""")]
    public async Task GetHistoricalDailyAsync_DoesNotConfirmUnknownOrFailedResponses(int status, string json)
    {
        foreach (var message in new[] { UnavailableMessage, BadRequestMessage })
        {
            using var handler = new ConfirmationHandler((HttpStatusCode)status, json);
            using var client = new HttpClient(handler);
            var provider = new YahooMarketDataProvider(
                (_, _, _) => throw new InvalidOperationException(message), client);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                provider.GetHistoricalDailyAsync("AAPL", Saturday, Sunday));
            Assert.Equal(status == 401 ? 2 : 1, handler.RequestCount);
        }
    }

    [Theory]
    [InlineData("network")]
    [InlineData("rate")]
    [InlineData("server")]
    [InlineData("authentication")]
    [InlineData("bad-request-http")]
    [InlineData("bad-request-unknown")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("null")]
    public async Task GetHistoricalDailyAsync_PropagatesUnclassifiedFailuresWithoutConfirmation(string failure)
    {
        var handler = new ConfirmationHandler(HttpStatusCode.OK, "{}");
        using var client = new HttpClient(handler);
        Exception expected = failure switch
        {
            "network" => new HttpRequestException(UnavailableMessage),
            "rate" => new HttpRequestException(UnavailableMessage, null, HttpStatusCode.TooManyRequests),
            "server" => new HttpRequestException(UnavailableMessage, null, HttpStatusCode.InternalServerError),
            "authentication" => new InvalidOperationException("Yahoo Finance Server Error Unauthorized"),
            "bad-request-http" => new HttpRequestException(BadRequestMessage, null, HttpStatusCode.BadRequest),
            "bad-request-unknown" => new InvalidOperationException(BadRequestMessage + ": invalid input"),
            "malformed" => new JsonException(UnavailableMessage),
            _ => new InvalidOperationException("Unknown provider failure")
        };
        var provider = new YahooMarketDataProvider((_, _, _) =>
            failure == "null" ? Task.FromResult<IReadOnlyList<MarketCandle>>(null!) : throw expected, client);

        var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
            provider.GetHistoricalDailyAsync("AAPL", Saturday, Sunday));
        if (failure != "null")
            Assert.Same(expected, actual);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetHistoricalDailyAsync_ConfirmationNetworkFailureRemainsFailure()
    {
        using var client = new HttpClient(new ConfirmationHandler(HttpStatusCode.OK, "{}") { Fail = true });
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            UnavailableProvider(client).GetHistoricalDailyAsync("AAPL", Saturday, Sunday));
    }

    private static YahooMarketDataProvider UnavailableProvider(HttpClient client) =>
        new((_, _, _) => throw new InvalidOperationException(UnavailableMessage), client);

    private sealed class MetadataFailureHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Query.Contains("range=")
                ? throw new HttpRequestException("Network unavailable")
                : base.SendAsync(request, cancellationToken);
    }

    private sealed class ConfirmationHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public Uri? Request { get; private set; }
        public string UserAgent { get; private set; } = "";
        public int RequestCount { get; private set; }
        public int SessionRequestCount { get; private set; }
        public bool Fail { get; init; }
        public bool RequireCrumb { get; init; }
        public int CrumbRequestCount { get; private set; }
        public HttpStatusCode CrumbStatus { get; init; } = HttpStatusCode.OK;
        public string Crumb { get; init; } = "test/session";
        public string? CrumbCookie { get; private set; }
        public string? ChartCookie { get; private set; }
        public HttpStatusCode MetadataStatus { get; init; } = HttpStatusCode.NotFound;
        public string MetadataJson { get; init; } =
            """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""";
        public Uri? MetadataRequest { get; private set; }
        public int MetadataRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgent = request.Headers.UserAgent.ToString();
            if (request.RequestUri!.Host == "fc.yahoo.com")
            {
                SessionRequestCount++;
                var response = new HttpResponseMessage(HttpStatusCode.NotFound);
                response.Headers.TryAddWithoutValidation("Set-Cookie", "A1=test-session; Path=/; Secure");
                return Task.FromResult(response);
            }
            if (request.RequestUri!.AbsolutePath == "/v1/test/getcrumb")
            {
                CrumbRequestCount++;
                CrumbCookie = request.Headers.GetValues("Cookie").FirstOrDefault();
                if (RequireCrumb && CrumbCookie != "A1=test-session")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
                return Task.FromResult(new HttpResponseMessage(CrumbStatus) { Content = new StringContent(Crumb) });
            }
            if (request.RequestUri!.Query.Contains("range="))
            {
                MetadataRequest = request.RequestUri;
                MetadataRequestCount++;
                return Task.FromResult(new HttpResponseMessage(MetadataStatus) { Content = new StringContent(MetadataJson) });
            }
            Request = request.RequestUri;
            RequestCount++;
            ChartCookie = request.Headers.Contains("Cookie")
                ? request.Headers.GetValues("Cookie").FirstOrDefault()
                : null;
            if (Fail)
                throw new HttpRequestException("Network unavailable");
            if (RequireCrumb && (RequestCount == 1 || !Request!.Query.Contains("&crumb=") ||
                ChartCookie != "A1=test-session"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
