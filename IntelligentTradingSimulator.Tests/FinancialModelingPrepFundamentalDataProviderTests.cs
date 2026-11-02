using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace IntelligentTradingSimulator.Tests;

public sealed class FinancialModelingPrepFundamentalDataProviderTests
{
    private const string ValidApiKey = "test-api-key";
    private const string Sentinel = "fake sentinel &+/?#";

    private static void AssertSecretSafe(FundamentalDataFetchException exception)
    {
        var diagnostic = exception + string.Join(" ", exception.Failures);
        Assert.DoesNotContain(Sentinel, diagnostic);
        Assert.DoesNotContain(Uri.EscapeDataString(Sentinel), diagnostic);
        Assert.DoesNotContain("https://", diagnostic);
        Assert.DoesNotContain("apikey", diagnostic);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_MapsAllFieldsAndConvertsUnits()
    {
        var growthJson = """
            [
              {
                "date": "2025-12-31",
                "symbol": "AAPL",
                "revenueGrowth": 0.125,
                "epsgrowth": 0.082
              }
            ]
            """;

        var ratiosJson = """
            [
              {
                "date": "2025-12-31",
                "symbol": "AAPL",
                "netProfitMargin": 0.235,
                "debtToEquityRatio": 1.45
              }
            ]
            """;

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            Assert.Contains("apikey=test-api-key", url);
            Assert.Contains("limit=1", url);

            if (url.Contains("/stable/financial-growth?symbol=AAPL&"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(growthJson)
                };
            }

            if (url.Contains("/stable/ratios?symbol=AAPL&"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ratiosJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync(" aapl ");

        Assert.NotNull(result);
        Assert.Equal("AAPL", result.Symbol);
        Assert.Equal(new DateOnly(2025, 12, 31), result.AsOfDate);
        Assert.Equal(12.5m, result.RevenueGrowthPercent);
        Assert.Equal(8.2m, result.EpsGrowthPercent);
        Assert.Equal(23.5m, result.Margins);
        Assert.Equal(1.45m, result.Debt);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_UsesOnlyMetricsFromTheNewerReportingPeriod()
    {
        var growthJson = """
            [
              {
                "date": "2024-12-31",
                "symbol": "AAPL",
                "revenueGrowth": 0.125,
                "epsgrowth": 0.082
              }
            ]
            """;

        var ratiosJson = """
            [
              {
                "date": "2025-12-31",
                "symbol": "AAPL",
                "netProfitMargin": 0.235,
                "debtEquityRatio": 1.45
              }
            ]
            """;

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    url.Contains("/stable/financial-growth?symbol=AAPL&") ? growthJson : ratiosJson)
            };
        });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("AAPL");

        Assert.NotNull(result);
        Assert.Equal(new DateOnly(2025, 12, 31), result.AsOfDate);
        Assert.Null(result.RevenueGrowthPercent);
        Assert.Null(result.EpsGrowthPercent);
        Assert.Equal(23.5m, result.Margins);
        Assert.Equal(1.45m, result.Debt);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_ReturnsNullWhenBothPeriodsAreUnknown()
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            var content = request.RequestUri!.AbsolutePath.Contains("financial-growth")
                ? """[{"symbol":"AAPL","revenueGrowth":0.125}]"""
                : """[{"symbol":"AAPL","netProfitMargin":0.235}]""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            };
        });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("AAPL");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_HandlesFallbackFieldNames()
    {
        var growthJson = """
            [
              {
                "date": "2024-09-30",
                "symbol": "MSFT",
                "revenueGrowthTTM": 0.10,
                "epsdilutedGrowth": 0.055
              }
            ]
            """;

        var ratiosJson = """
            [
              {
                "date": "2024-09-30",
                "symbol": "MSFT",
                "netProfitMarginTTM": 0.182,
                "debtToEquity": 0.85
              }
            ]
            """;

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/stable/financial-growth?symbol=MSFT&"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(growthJson)
                };
            }

            if (url.Contains("/stable/ratios?symbol=MSFT&"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(ratiosJson)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("MSFT");

        Assert.NotNull(result);
        Assert.Equal("MSFT", result.Symbol);
        Assert.Equal(new DateOnly(2024, 9, 30), result.AsOfDate);
        Assert.Equal(10.0m, result.RevenueGrowthPercent);
        Assert.Equal(5.5m, result.EpsGrowthPercent);
        Assert.Equal(18.2m, result.Margins);
        Assert.Equal(0.85m, result.Debt);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetFundamentalDataAsync_ReturnsNullWhenBothEndpointsReturnEmpty(string content)
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("UNKNOWN");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_ReturnsNullWhenBothEndpointsReturn404()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("UNKNOWN");

        Assert.Null(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetFundamentalDataAsync_ReturnsPartialWhenOnlyOneEndpointSucceeds(bool growthSucceeds)
    {
        var growthJson = """
            [
              {
                "date": "2025-06-30",
                "symbol": "XYZ",
                "revenueGrowth": 0.05
              }
            ]
            """;

        var handler = new MockHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/stable/financial-growth?symbol=XYZ&") == growthSucceeds)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(growthSucceeds
                        ? growthJson
                        : """[{"date":"2025-06-30","netProfitMargin":0.20,"debtEquityRatio":0.75}]""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var provider = CreateProvider(handler);

        var result = await provider.GetFundamentalDataAsync("XYZ");

        Assert.NotNull(result);
        Assert.Equal("XYZ", result.Symbol);
        Assert.Equal(new DateOnly(2025, 6, 30), result.AsOfDate);
        Assert.Equal(growthSucceeds ? 5.0m : (decimal?)null, result.RevenueGrowthPercent);
        Assert.Null(result.EpsGrowthPercent);
        Assert.Equal(growthSucceeds ? (decimal?)null : 20m, result.Margins);
        Assert.Equal(growthSucceeds ? (decimal?)null : 0.75m, result.Debt);
    }

    [Theory]
    [InlineData("https://financialmodelingprep.com/", "/stable/")]
    [InlineData("https://example.test/fmp", "/fmp/stable/")]
    public async Task GetFundamentalDataAsync_UsesStableEndpointsWithEncodedQueryParameters(
        string baseUrl,
        string expectedPathPrefix)
    {
        var requestedPaths = new List<string>();
        var handler = new MockHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!;
            Assert.Equal(new Uri(baseUrl).Host, uri.Host);
            Assert.Equal("?symbol=BRK%2FB%20%26%2B%3F%23&limit=1&apikey=key%20%26%2B%2F%3F%23", uri.Query);
            requestedPaths.Add(uri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            };
        });
        var provider = CreateProvider(handler, " key &+/?# ", baseUrl);

        await provider.GetFundamentalDataAsync(" brk/b &+?# ");

        Assert.Equal(
            new[] { expectedPathPrefix + "financial-growth", expectedPathPrefix + "ratios" },
            requestedPaths.OrderBy(path => path, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Error Message")]
    [InlineData("errorMessage")]
    [InlineData("error")]
    public async Task GetFundamentalDataAsync_PropagatesProviderJsonError(string errorProperty)
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        [errorProperty] = $"https://example.test/?apikey={Uri.EscapeDataString(Sentinel)} {Sentinel}"
                    }))
            });
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Equal(2, exception.Failures.Count);
        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(FundamentalDataFailureCategory.ProviderError, failure.Category);
            Assert.Equal(200, failure.HttpStatus);
        });
        AssertSecretSafe(exception);
    }

    [Theory]
    [InlineData(401, FundamentalDataFailureCategory.AuthAccess)]
    [InlineData(403, FundamentalDataFailureCategory.AuthAccess)]
    [InlineData(429, FundamentalDataFailureCategory.RateLimit)]
    [InlineData(500, FundamentalDataFailureCategory.UpstreamServer)]
    [InlineData(503, FundamentalDataFailureCategory.UpstreamServer)]
    [InlineData(400, FundamentalDataFailureCategory.HttpFailure)]
    public async Task GetFundamentalDataAsync_ReportsBothHttpFailuresWithoutUpstreamText(
        int status, FundamentalDataFailureCategory category)
    {
        var calls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent($"{Sentinel} {Uri.EscapeDataString(Sentinel)}"),
                ReasonPhrase = Uri.EscapeDataString(Sentinel)
            };
        });

        var provider = CreateProvider(handler, Sentinel);

        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Equal(2, calls);
        Assert.Equal(new[] { FundamentalDataEndpoint.FinancialGrowth, FundamentalDataEndpoint.Ratios },
            exception.Failures.Select(failure => failure.Endpoint));
        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(category, failure.Category);
            Assert.Equal(status, failure.HttpStatus);
        });
        AssertSecretSafe(exception);
    }

    [Theory]
    [InlineData(403, FundamentalDataFailureCategory.AuthAccess)]
    [InlineData(429, FundamentalDataFailureCategory.RateLimit)]
    [InlineData(404, null)]
    public async Task GetFundamentalDataAsync_ClassifiesErrorHeadersWithoutReadingBody(
        int status, FundamentalDataFailureCategory? category)
    {
        var bodyReads = 0;
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new CallbackHttpContent(_ =>
                {
                    bodyReads++;
                    throw new IOException(Sentinel);
                })
            });
        var provider = CreateProvider(handler, Sentinel);

        if (category.HasValue)
        {
            var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
                () => provider.GetFundamentalDataAsync("AAPL"));
            Assert.Equal(2, exception.Failures.Count);
            Assert.All(exception.Failures, failure =>
            {
                Assert.Equal(status, failure.HttpStatus);
                Assert.Equal(category.Value, failure.Category);
            });
            AssertSecretSafe(exception);
        }
        else
        {
            Assert.Null(await provider.GetFundamentalDataAsync("AAPL"));
        }

        Assert.Equal(0, bodyReads);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_TimesOutStalledSuccessfulBodyAndRetainsStatus()
    {
        var bodyReads = 0;
        using var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CallbackHttpContent(async token =>
                {
                    Interlocked.Increment(ref bodyReads);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                })
            });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        var provider = new FinancialModelingPrepFundamentalDataProvider(
            client, Options.Create(new FinancialModelingPrepOptions { ApiKey = Sentinel }));

        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => provider.GetFundamentalDataAsync("AAPL").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, bodyReads);
        Assert.Equal(2, exception.Failures.Count);
        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(200, failure.HttpStatus);
            Assert.Equal(FundamentalDataFailureCategory.Timeout, failure.Category);
        });
        AssertSecretSafe(exception);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesCallerCancellationDuringBodyRead()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new CallbackHttpContent(token =>
                {
                    cts.Cancel();
                    return Task.FromCanceled(token);
                })
            });

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProvider(handler, Sentinel).GetFundamentalDataAsync("AAPL", cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Sentinel, exception.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetFundamentalDataAsync_AttributesSingleFailureToCorrectEndpoint(bool growthFails)
    {
        var handler = new MockHttpMessageHandler(request =>
            new HttpResponseMessage(request.RequestUri!.AbsolutePath.Contains("financial-growth") == growthFails
                ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });
        var provider = CreateProvider(handler);
        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        var failure = Assert.Single(exception.Failures);
        Assert.Equal(growthFails ? FundamentalDataEndpoint.FinancialGrowth : FundamentalDataEndpoint.Ratios,
            failure.Endpoint);
        Assert.Equal(403, failure.HttpStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetFundamentalDataAsync_PreservesConcurrentFailuresRegardlessOfCompletionOrder(bool growthFirst)
    {
        var growth = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ratios = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<string>();
        var handler = new AsyncHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return request.RequestUri.AbsolutePath.Contains("financial-growth") ? growth.Task : ratios.Task;
        });
        var pending = CreateProvider(handler).GetFundamentalDataAsync("AAPL");
        Assert.Equal(2, requests.Count);
        if (growthFirst)
            growth.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        else
            ratios.SetResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        Assert.False(pending.IsCompleted);
        if (growthFirst)
            ratios.SetResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        else
            growth.SetResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(() => pending);
        Assert.Collection(exception.Failures,
            failure =>
            {
                Assert.Equal(FundamentalDataEndpoint.FinancialGrowth, failure.Endpoint);
                Assert.Equal(401, failure.HttpStatus);
            },
            failure =>
            {
                Assert.Equal(FundamentalDataEndpoint.Ratios, failure.Endpoint);
                Assert.Equal(429, failure.HttpStatus);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData(403)]
    public async Task GetFundamentalDataAsync_SanitizesTransportException(int? status)
    {
        var handler = new MockHttpMessageHandler(request =>
            throw new HttpRequestException($"{request.RequestUri} {Sentinel}",
                new Exception(Sentinel), (HttpStatusCode?)status));
        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => CreateProvider(handler, Sentinel).GetFundamentalDataAsync("AAPL"));

        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(status, failure.HttpStatus);
            Assert.Equal(status.HasValue ? FundamentalDataFailureCategory.AuthAccess
                : FundamentalDataFailureCategory.Transport, failure.Category);
        });
        AssertSecretSafe(exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetFundamentalDataAsync_ReportsInvalidJsonWithoutRawPayload(bool array)
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(array
                ? JsonSerializer.Serialize(new[] { Sentinel, Uri.EscapeDataString(Sentinel) })
                : $"{{{Sentinel} {Uri.EscapeDataString(Sentinel)}")
        });
        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => CreateProvider(handler).GetFundamentalDataAsync("AAPL"));

        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(FundamentalDataFailureCategory.InvalidJson, failure.Category);
            Assert.Equal(200, failure.HttpStatus);
        });
        AssertSecretSafe(exception);
    }

    [Theory]
    [InlineData(true, null, 403)]
    [InlineData(false, null, 403)]
    [InlineData(true, "utf-7", 403)]
    [InlineData(false, "utf-7", 403)]
    [InlineData(true, "utf-7", 429)]
    [InlineData(false, "utf-7", 429)]
    public async Task GetFundamentalDataAsync_SanitizesInvalidEncodingAndRetainsOtherEndpointFailure(
        bool growthInvalid, string? charset, int otherStatus)
    {
        var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("financial-growth") != growthInvalid)
                return new HttpResponseMessage((HttpStatusCode)otherStatus);

            var content = new StringContent($"{Sentinel} {Uri.EscapeDataString(Sentinel)}");
            content.Headers.ContentType!.CharSet = charset ?? "unsupported-" + Uri.EscapeDataString(Sentinel);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => CreateProvider(handler, Sentinel).GetFundamentalDataAsync("AAPL"));

        Assert.Equal(2, exception.Failures.Count);
        Assert.All(exception.Failures, failure =>
        {
            var invalid = (failure.Endpoint == FundamentalDataEndpoint.FinancialGrowth) == growthInvalid;
            Assert.Equal(invalid ? 200 : otherStatus, failure.HttpStatus);
            Assert.Equal(invalid ? FundamentalDataFailureCategory.InvalidJson
                : otherStatus == 403 ? FundamentalDataFailureCategory.AuthAccess
                : FundamentalDataFailureCategory.RateLimit, failure.Category);
        });
        AssertSecretSafe(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetFundamentalDataAsync_ThrowsIfApiKeyNotConfigured(string? apiKey)
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });

        var options = Options.Create(new FinancialModelingPrepOptions { ApiKey = apiKey! });
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://financialmodelingprep.com/") };
        var provider = new FinancialModelingPrepFundamentalDataProvider(httpClient, options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Contains("API key", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetFundamentalDataAsync_RejectsBlankSymbol(string? symbol)
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK));

        var provider = CreateProvider(handler);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => provider.GetFundamentalDataAsync(symbol!));
    }

    [Fact]
    public async Task GetFundamentalDataAsync_HonorsCancellation()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });

        var provider = CreateProvider(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetFundamentalDataAsync("AAPL", cts.Token));
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesHttpClientTimeout()
    {
        var handler = new MockHttpMessageHandler(request =>
            throw new TaskCanceledException($"{request.RequestUri} {Sentinel}",
                new TimeoutException(Sentinel)));
        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => CreateProvider(handler, Sentinel).GetFundamentalDataAsync("AAPL"));

        Assert.All(exception.Failures, failure =>
        {
            Assert.Equal(FundamentalDataFailureCategory.Timeout, failure.Category);
            Assert.Null(failure.HttpStatus);
        });
        AssertSecretSafe(exception);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesInFlightCallerCancellationWithoutSecrets()
    {
        using var cts = new CancellationTokenSource();
        var handler = new MockHttpMessageHandler(request =>
        {
            cts.Cancel();
            throw new OperationCanceledException($"{request.RequestUri} {Sentinel}",
                new Exception(Sentinel), cts.Token);
        });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProvider(handler, Sentinel).GetFundamentalDataAsync("AAPL", cts.Token));

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Sentinel, exception.ToString());
        Assert.DoesNotContain(Uri.EscapeDataString(Sentinel), exception.ToString());
    }

    [Fact]
    public async Task AddInfrastructure_DisablesOnlyFmpRequestLogging()
    {
        var services = new ServiceCollection();
        var logs = new CapturingLoggerProvider();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FundamentalData:FinancialModelingPrep:ApiKey"] = Sentinel
        }).Build();
        services.AddInfrastructure("Server=localhost;Database=Unused;", config);
        services.AddHttpClient(nameof(FinancialModelingPrepFundamentalDataProvider))
            .ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler(request =>
                throw new HttpRequestException($"{request.RequestUri} {Sentinel}")));
        services.AddHttpClient("unrelated")
            .ConfigurePrimaryHttpMessageHandler(() => new MockHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)));
        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IFundamentalDataProvider>();

        var exception = await Assert.ThrowsAsync<FundamentalDataFetchException>(
            () => provider.GetFundamentalDataAsync("AAPL"));
        AssertSecretSafe(exception);
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains(nameof(FinancialModelingPrepFundamentalDataProvider)));
        Assert.DoesNotContain(logs.Messages, message =>
            message.Contains(Sentinel) || message.Contains(Uri.EscapeDataString(Sentinel)) ||
            message.Contains("apikey"));

        using var response = await serviceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("unrelated").GetAsync("https://example.test/no-secret");
        Assert.Contains(logs.Messages, message => message.Contains("System.Net.Http.HttpClient.unrelated"));
    }

    [Fact]
    public void AddInfrastructure_RegistersFmpProvider_WhenApiKeyConfigured()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["FundamentalData:FinancialModelingPrep:ApiKey"] = "valid-key",
            ["FundamentalData:FinancialModelingPrep:BaseUrl"] = "https://financialmodelingprep.com/",
            ["FundamentalData:FinancialModelingPrep:RequestTimeoutSeconds"] = "4"
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        IntelligentTradingSimulator.Infrastructure.ServiceCollectionExtensions.AddInfrastructure(
            services,
            "Server=localhost;Database=TradingDb;",
            config);

        var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetService<IntelligentTradingSimulator.Core.Interfaces.IFundamentalDataProvider>();

        Assert.NotNull(provider);
        Assert.IsType<FinancialModelingPrepFundamentalDataProvider>(provider);
        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(FinancialModelingPrepFundamentalDataProvider));
        Assert.Equal(TimeSpan.FromSeconds(4), httpClient.Timeout);
    }

    [Fact]
    public void AddInfrastructure_UsesLegacyFmpSectionWhenPrimarySectionIsMissing()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["FinancialModelingPrep:ApiKey"] = "legacy-key"
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        IntelligentTradingSimulator.Infrastructure.ServiceCollectionExtensions.AddInfrastructure(
            services,
            "Server=localhost;Database=TradingDb;",
            config);

        using var serviceProvider = services.BuildServiceProvider();

        Assert.IsType<FinancialModelingPrepFundamentalDataProvider>(
            serviceProvider.GetRequiredService<IFundamentalDataProvider>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddInfrastructure_DoesNotRegisterFmpProvider_WhenApiKeyEmptyOrMissing(string? apiKey)
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["FundamentalData:FinancialModelingPrep:ApiKey"] = apiKey
        };
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        IntelligentTradingSimulator.Infrastructure.ServiceCollectionExtensions.AddInfrastructure(
            services,
            "Server=localhost;Database=TradingDb;",
            config);

        var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetService<IntelligentTradingSimulator.Core.Interfaces.IFundamentalDataProvider>();

        Assert.Null(provider);
    }

    private static FinancialModelingPrepFundamentalDataProvider CreateProvider(
        HttpMessageHandler handler,
        string apiKey = ValidApiKey,
        string baseUrl = "https://financialmodelingprep.com/")
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://financialmodelingprep.com/")
        };

        var options = Options.Create(new FinancialModelingPrepOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl
        });

        return new FinancialModelingPrepFundamentalDataProvider(httpClient, options);
    }

    private sealed class CallbackHttpContent(Func<CancellationToken, Task> serialize) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            serialize(CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            serialize(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class AsyncHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request, cancellationToken);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(category + ": " + formatter(state, exception) + exception);
        }
    }
}
