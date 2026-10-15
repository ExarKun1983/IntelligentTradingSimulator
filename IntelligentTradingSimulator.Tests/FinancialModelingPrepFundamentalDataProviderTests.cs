using System.Net;
using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Infrastructure;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntelligentTradingSimulator.Tests;

public sealed class FinancialModelingPrepFundamentalDataProviderTests
{
    private const string ValidApiKey = "test-api-key";

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
                "debtEquityRatio": 1.45
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
                    new Dictionary<string, string> { [errorProperty] = "Provider rejected request." }))
            });
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Contains("Provider rejected request.", exception.Message);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesUnauthorizedHttpError()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                ReasonPhrase = "Unauthorized"
            });

        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task GetFundamentalDataAsync_PropagatesServerError()
    {
        var handler = new MockHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                ReasonPhrase = "Internal Server Error"
            });

        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetFundamentalDataAsync("AAPL"));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
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
        using var httpClient = new HttpClient(new DelayingHttpMessageHandler())
        {
            BaseAddress = new Uri("https://financialmodelingprep.com/"),
            Timeout = TimeSpan.FromMilliseconds(20)
        };
        var provider = new FinancialModelingPrepFundamentalDataProvider(
            httpClient,
            Options.Create(new FinancialModelingPrepOptions { ApiKey = ValidApiKey }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetFundamentalDataAsync("AAPL"));
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

    private sealed class DelayingHttpMessageHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
