using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace IntelligentTradingSimulator.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ITS_SQLSERVER_TEST_CONNECTION_STRING")))
            Skip = "Set ITS_SQLSERVER_TEST_CONNECTION_STRING to run SQL Server concurrency tests.";
    }
}

public sealed class MarketCandlePersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2024, 1, 1);
    private const string Source = HistoricalMarketDataPolicy.Source;
    private const string Mode = HistoricalMarketDataPolicy.PriceAdjustmentMode;
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<TradingSimulatorDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(_connection).Options;
        await using var context = NewContext();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();
    private TradingSimulatorDbContext NewContext() => new(_options);
    private static MarketCandle Candle(int day, decimal close = 100m) => new()
    {
        Date = Start.AddDays(day), Open = close, High = close + 1m,
        Low = close - 1m, Close = close, Volume = 1000m
    };

    [Fact]
    public void Migration_AddsOnlyMarketDataTablesAndUniqueIndexes_AndCanBeReversed()
    {
        var migration = new AddMarketCandles();
        var tables = migration.UpOperations.OfType<CreateTableOperation>().ToList();
        Assert.Equal(2, tables.Count);
        var candles = Assert.Single(tables, x => x.Name == "MarketCandles");
        Assert.Equal("date", Assert.Single(candles.Columns, x => x.Name == "Date").ColumnType);
        Assert.Equal(12, Assert.Single(candles.Columns, x => x.Name == "Close").Scale);
        Assert.Contains(candles.Columns, x => x.Name == "DownloadedAt" && !x.IsNullable);
        Assert.Contains(candles.Columns, x => x.Name == "PriceAdjustmentMode" && !x.IsNullable);
        Assert.Contains(tables, x => x.Name == "MarketDataCoverage");
        var indexes = migration.UpOperations.OfType<CreateIndexOperation>().ToList();
        Assert.Equal(2, indexes.Count);
        Assert.All(indexes, x => Assert.True(x.IsUnique));
        Assert.Contains(indexes, x => x.Table == "MarketCandles" &&
            x.Columns.SequenceEqual(["Symbol", "Source", "Date"]));
        Assert.Equal(new[] { "MarketCandles", "MarketDataCoverage" },
            migration.DownOperations.OfType<DropTableOperation>().Select(x => x.Name));

        using var sqlServerContext = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        Assert.False(sqlServerContext.Database.HasPendingModelChanges());
        var query = sqlServerContext.MarketCandles.Where(x =>
            x.Symbol == "AAPL" && x.Source == Source && x.Date >= Start && x.Date <= Start.AddDays(3))
            .OrderBy(x => x.Date).ToQueryString();
        Assert.Contains("[MarketCandles]", query);
        Assert.Contains("[Source]", query);
        Assert.Contains("ORDER BY", query);
    }

    [Fact]
    public async Task Repository_RoundTripsRangeAcrossContexts_AndBulkUpsertPreservesSnapshot()
    {
        DateTime downloadedAt;
        await using (var context = NewContext())
        {
            var repository = new MarketCandleRepository(context);
            await repository.UpsertAsync(" aapl ", Source, Mode, Start, Start.AddDays(4),
                [Candle(0), Candle(2), Candle(2), Candle(4)]);
            downloadedAt = (await context.MarketCandles.FirstAsync()).DownloadedAt;
            await repository.UpsertAsync("AAPL", Source, Mode, Start, Start.AddDays(4),
                [Candle(0, 200), Candle(2, 200), Candle(4, 200)]);
            Assert.Equal(3, await context.MarketCandles.CountAsync());
            Assert.Single(await context.MarketDataCoverage.ToListAsync());
        }
        await using var readContext = NewContext();
        var readRepository = new MarketCandleRepository(readContext);
        var result = Assert.Single(await readRepository.GetRangeAsync(
            "aapl", Source, Mode, Start.AddDays(1), Start.AddDays(3)));
        Assert.Equal(Start.AddDays(2), result.Date);
        Assert.Equal(100m, result.Open);
        Assert.Equal(101m, result.High);
        Assert.Equal(99m, result.Low);
        Assert.Equal(100m, result.Close);
        Assert.Equal(1000m, result.Volume);
        Assert.Empty(await readRepository.GetRangeAsync("MSFT", Source, Mode, Start, Start.AddDays(4)));
        Assert.Empty(await readRepository.GetRangeAsync("AAPL", "OtherSource", Mode, Start, Start.AddDays(4)));
        var rows = await readContext.MarketCandles.AsNoTracking().ToListAsync();
        Assert.All(rows, x =>
        {
            Assert.Equal("AAPL", x.Symbol);
            Assert.Equal(Source, x.Source);
            Assert.Equal(Mode, x.PriceAdjustmentMode);
            Assert.Equal(downloadedAt, x.DownloadedAt);
        });
    }

    [Fact]
    public async Task Database_UniqueIndexRejectsDuplicateIdentity_ButAllowsAnotherSource()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await repository.UpsertAsync("AAPL", Source, Mode, Start, Start, [Candle(0)]);
        await repository.UpsertAsync("AAPL", "OtherSource", Mode, Start, Start, [Candle(0, 200)]);
        context.MarketCandles.Add(new MarketCandleEntity
        {
            Symbol = "AAPL", Source = Source, PriceAdjustmentMode = Mode, Date = Start
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Theory]
    [InlineData("price")]
    [InlineData("high")]
    [InlineData("low")]
    [InlineData("volume")]
    [InlineData("date")]
    [InlineData("conflict")]
    public async Task Repository_InvalidBatchDoesNotPersistCandlesOrCoverage(string invalid)
    {
        await using var context = NewContext();
        var candle = Candle(1);
        switch (invalid)
        {
            case "price": candle.Close = 0; break;
            case "high": candle.High = 99; break;
            case "low": candle.Low = 101; break;
            case "volume": candle.Volume = -1; break;
            case "date": candle.Date = Start.AddDays(10); break;
        }
        IReadOnlyList<MarketCandle> batch = invalid == "conflict"
            ? [Candle(0), Candle(0, 200)] : [Candle(0), candle];
        await Assert.ThrowsAsync<ArgumentException>(() => new MarketCandleRepository(context)
            .UpsertAsync("AAPL", Source, Mode, Start, Start.AddDays(2), batch));
        Assert.Empty(await context.MarketCandles.ToListAsync());
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
    }

    [Fact]
    public async Task Repository_RejectsIncompatibleAdjustmentMode()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await repository.UpsertAsync("AAPL", Source, Mode, Start, Start, [Candle(0)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.GetRangeAsync("AAPL", Source, "AdjustedClose", Start, Start));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.UpsertAsync("AAPL", Source, "AdjustedClose", Start, Start, [Candle(0)]));
        Assert.Single(await context.MarketCandles.ToListAsync());
    }

    [Fact]
    public async Task Repository_RejectsIncompatibleModeOverCoveredEmptyDates()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await repository.UpsertAsync("AAPL", Source, Mode, Start, Start.AddDays(2), [Candle(0)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.UpsertAsync("AAPL", Source, "AdjustedClose", Start.AddDays(1), Start.AddDays(1),
                [Candle(1, 200)]));

        var candles = await repository.GetRangeAsync("AAPL", Source, Mode, Start, Start.AddDays(2));
        Assert.Equal(Start, Assert.Single(candles).Date);
        Assert.Equal(100m, candles[0].Close);
        var coverage = Assert.Single(await context.MarketDataCoverage.ToListAsync());
        Assert.Equal(Mode, coverage.PriceAdjustmentMode);
        Assert.Equal(Start, coverage.From);
        Assert.Equal(Start.AddDays(2), coverage.To);
    }

    [Fact]
    public async Task Service_SecondProcessAndOverlappingStrategyRangeUseIdenticalDatabaseSnapshot()
    {
        var provider = new FakeProvider { Candles = [Candle(0), Candle(1), Candle(3)] };
        await using (var context = NewContext())
        {
            var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
            var first = await service.GetHistoricalDailyRangeAsync(" aapl ", Start, Start.AddDays(5));
            Assert.Equal(3, first.Count);
        }
        Assert.Single(provider.Requests);
        provider.Candles = [Candle(0, 500), Candle(1, 500), Candle(3, 500)];
        await using var nextContext = NewContext();
        var progress = new RecordingProgress();
        var countingRepository = new CountingRepository(new MarketCandleRepository(nextContext));
        var nextService = new BacktestHistoricalMarketDataService(provider, countingRepository, progress);
        var overlap = await nextService.GetHistoricalDailyUpToAsync("AAPL", Start.AddDays(1), Start.AddDays(5), Start.AddDays(2));
        Assert.Equal(100m, Assert.Single(overlap).Close);
        Assert.Single(provider.Requests);
        Assert.Equal(1, countingRepository.RangeReadCount);
        Assert.Contains(progress.Messages, x => x.EndsWith("| database"));
    }

    [Fact]
    public async Task Service_FetchesOnlyLeadingInternalAndTrailingGaps()
    {
        await using var context = NewContext();
        var repository = new CountingRepository(new MarketCandleRepository(context));
        await repository.UpsertAsync("AAPL", Source, Mode, Start.AddDays(2), Start.AddDays(3), [Candle(2)]);
        await repository.UpsertAsync("AAPL", Source, Mode, Start.AddDays(6), Start.AddDays(7), [Candle(6)]);
        var provider = new FakeProvider
        {
            // A deliberately unbounded provider response must still be filtered.
            Candles = Enumerable.Range(0, 12).Select(x => Candle(x, 200)).ToArray()
        };
        var progress = new RecordingProgress();
        var service = new BacktestHistoricalMarketDataService(provider, repository, progress);
        var result = await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(9));
        Assert.Equal(new[]
        {
            (Start, Start.AddDays(1)),
            (Start.AddDays(4), Start.AddDays(5)),
            (Start.AddDays(8), Start.AddDays(9))
        }, provider.Requests);
        Assert.Equal(8, result.Count);
        Assert.Equal(2, repository.RangeReadCount);
        Assert.Equal(100, result.Single(x => x.Date == Start.AddDays(2)).Close);
        Assert.Equal(8, await context.MarketCandles.CountAsync());
        Assert.Contains(progress.Messages, x => x.EndsWith("| database and external provider"));
        var repeated = new BacktestHistoricalMarketDataService(provider, repository);
        await repeated.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(9));
        Assert.Equal(3, provider.Requests.Count);
    }

    [Fact]
    public async Task Service_EmptyWeekendIsPersistentlyCovered()
    {
        var provider = new FakeProvider();
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await new BacktestHistoricalMarketDataService(provider, repository)
            .GetHistoricalDailyRangeAsync("AAPL", Start.AddDays(5), Start.AddDays(6));
        var result = await new BacktestHistoricalMarketDataService(provider, repository)
            .GetHistoricalDailyRangeAsync("AAPL", Start.AddDays(5), Start.AddDays(6));
        Assert.Empty(result);
        Assert.Single(provider.Requests);
        Assert.Single(await context.MarketDataCoverage.ToListAsync());
    }

    [Theory]
    [InlineData("AAPL", 5)]
    [InlineData("MSFT", 6)]
    [InlineData("SPY", 5)]
    [InlineData("SPY", 6)]
    [InlineData("AAPL", 14)]
    public async Task Service_ConfirmedEmptyTailReturnsPersistedCandlesAndIsNotRequestedAgain(string symbol, int tailDay)
    {
        // 2024-01-05 is Friday; 2024-01-15 is the Martin Luther King Jr. holiday.
        var lastTradingDay = tailDay == 14 ? 11 : 4;
        await using (var context = NewContext())
        {
            var repository = new MarketCandleRepository(context);
            await repository.UpsertAsync(symbol, Source, Mode, Start, Start.AddDays(lastTradingDay),
                [Candle(0), Candle(lastTradingDay)]);
        }
        using var handler = new EmptyChartHandler(symbol);
        using var client = new HttpClient(handler);
        var requests = new List<(DateTime From, DateTime To)>();
        var provider = new YahooMarketDataProvider((_, from, to) =>
        {
            requests.Add((from.Date, to!.Value.Date.AddDays(-1)));
            throw new InvalidOperationException("Requested Information Not Available On Yahoo Finance");
        }, client);

        await using (var context = NewContext())
        {
            var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
            var result = await service.GetHistoricalDailyUpToAsync(
                symbol, Start, Start.AddDays(tailDay), Start.AddDays(tailDay));
            Assert.Equal(new[] { Start, Start.AddDays(lastTradingDay) }, result.Select(x => x.Date));
            Assert.All(result, x => Assert.Equal(100m, x.Close));
            Assert.Single(await service.GetHistoricalDailyUpToAsync(
                symbol, Start, Start.AddDays(tailDay), Start));
        }
        await using var nextContext = NewContext();
        var nextService = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(nextContext));
        Assert.Equal(2, (await nextService.GetHistoricalDailyUpToAsync(
            symbol, Start, Start.AddDays(tailDay), Start.AddDays(tailDay))).Count);
        Assert.Equal(2, (await nextService.GetHistoricalDailyUpToAsync(
            symbol, Start, Start.AddDays(tailDay - 1), Start.AddDays(tailDay - 1))).Count);
        Assert.Equal((Start.AddDays(lastTradingDay + 1), Start.AddDays(tailDay)), Assert.Single(requests));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(2, await nextContext.MarketCandles.CountAsync());
        var coverage = await nextContext.MarketDataCoverage.OrderBy(x => x.From).ToListAsync();
        Assert.Equal(2, coverage.Count);
        Assert.Equal(Start.AddDays(lastTradingDay + 1), coverage[1].From);
        Assert.Equal(Start.AddDays(tailDay), coverage[1].To);
    }

    [Fact]
    public async Task Service_ConfirmedWeekendDoesNotHideNextMondayTradingSession()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await repository.UpsertAsync("AAPL", Source, Mode, Start, Start.AddDays(4), [Candle(4)]);
        var provider = new FakeProvider();
        var service = new BacktestHistoricalMarketDataService(provider, repository);
        Assert.Single(await service.GetHistoricalDailyUpToAsync("AAPL", Start, Start.AddDays(6), Start.AddDays(6)));
        provider.Candles = [Candle(7, 110)];
        var monday = await service.GetHistoricalDailyUpToAsync("AAPL", Start, Start.AddDays(7), Start.AddDays(7));
        Assert.Equal(new[] { Start.AddDays(4), Start.AddDays(7) }, monday.Select(x => x.Date));
        Assert.Equal(110m, monday[^1].Close);
        Assert.Equal(new[]
        {
            (Start.AddDays(5), Start.AddDays(6)),
            (Start.AddDays(7), Start.AddDays(7))
        }, provider.Requests);
        await service.GetHistoricalDailyUpToAsync("AAPL", Start, Start.AddDays(6), Start.AddDays(6));
        Assert.Equal(2, provider.Requests.Count);
    }

    [Theory]
    [InlineData("PLTR")]
    [InlineData("SNOW")]
    public async Task Service_ConfirmedPreListingRangePersistsCoverageWithoutCandles(string symbol)
    {
        using var handler = new EmptyChartHandler(symbol, preListing: true);
        using var client = new HttpClient(handler);
        var provider = new YahooMarketDataProvider(
            (_, _, _) => throw new InvalidOperationException("Requested Information Not Available On Yahoo Finance"), client);
        await using (var context = NewContext())
        {
            var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
            for (var day = 10; day <= 12; day++)
                Assert.Empty(await service.GetHistoricalDailyUpToAsync(
                    symbol, new DateTime(2020, 1, 11), new DateTime(2020, 1, 12), new DateTime(2020, 1, day)));
        }
        await using var nextContext = NewContext();
        Assert.Empty(await new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(nextContext))
            .GetHistoricalDailyRangeAsync(symbol, new DateTime(2020, 1, 11), new DateTime(2020, 1, 12)));
        var coverage = Assert.Single(await nextContext.MarketDataCoverage.ToListAsync());
        Assert.Equal(symbol, coverage.Symbol);
        Assert.Equal(new DateTime(2020, 1, 11), coverage.From);
        Assert.Equal(new DateTime(2020, 1, 12), coverage.To);
        Assert.Empty(await nextContext.MarketCandles.ToListAsync());
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_PltrBadRequestConfirmsRollingPreListingGapWithoutChangingListedCandles(bool yahooBadRequest)
    {
        var listingDate = new DateTime(2020, 9, 30);
        var existingStart = new DateTime(2019, 5, 15);
        var end = new DateTime(2024, 12, 31);
        string snapshot;
        await using (var context = NewContext())
        {
            var first = Candle(0);
            first.Date = listingDate;
            var last = Candle(0, 200m);
            last.Date = end;
            await new MarketCandleRepository(context).UpsertAsync(
                "PLTR", Source, Mode, existingStart, end, [first, last]);
            snapshot = JsonSerializer.Serialize(await context.MarketCandles.AsNoTracking().OrderBy(x => x.Date).ToListAsync());
        }
        using var handler = new EmptyChartHandler("PLTR", preListing: true, badRequest: yahooBadRequest);
        using var client = new HttpClient(handler);
        var requests = new List<(DateTime From, DateTime To)>();
        var provider = new YahooMarketDataProvider((symbol, from, to) =>
        {
            Assert.Equal("PLTR", symbol);
            requests.Add((from.Date, to!.Value.Date.AddDays(-1)));
            throw new InvalidOperationException("Yahoo Finance Server Error BadRequest");
        }, client);

        for (var day = 25; day <= 30; day++)
        {
            var simulatedDate = new DateTime(2020, 9, day);
            var from = MarketHistoryRequirements.GetEffectiveStartDate(new DateTime(2020, 1, 1), simulatedDate);
            Assert.Equal(new DateTime(2019, 5, day - 15), from);
            // Use a fresh service/context to prove persisted coverage, not just an exact-range cache hit.
            await using var context = NewContext();
            var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
            var result = await service.GetHistoricalDailyUpToAsync("PLTR", from, end, simulatedDate);
            if (day < 30)
                Assert.Empty(result);
            else
            {
                Assert.Equal(listingDate, Assert.Single(result).Date);
                Assert.Equal(100m, result[0].Close);
            }
            Assert.Equal((new DateTime(2019, 5, 10), new DateTime(2019, 5, 14)), Assert.Single(requests));
        }

        await using var verifyContext = NewContext();
        var coverage = await verifyContext.MarketDataCoverage.OrderBy(x => x.From).ToListAsync();
        Assert.Equal(2, coverage.Count);
        Assert.Equal(new DateTime(2019, 5, 10), coverage[0].From);
        Assert.Equal(new DateTime(2019, 5, 14), coverage[0].To);
        Assert.Equal(existingStart, coverage[1].From);
        Assert.Equal(end, coverage[1].To);
        Assert.Equal(snapshot, JsonSerializer.Serialize(
            await verifyContext.MarketCandles.AsNoTracking().OrderBy(x => x.Date).ToListAsync()));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(yahooBadRequest ? 1 : 0, handler.MetadataRequestCount);
    }

    [Fact]
    public async Task Service_UnknownTickerExactBadRequestDoesNotCreateCoverageAndRemainsRetryable()
    {
        using var handler = new EmptyChartHandler("ZZZXQ", preListing: true, badRequest: true, knownSymbol: false);
        using var client = new HttpClient(handler);
        var calls = 0;
        var provider = new YahooMarketDataProvider((_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("Yahoo Finance Server Error BadRequest");
        }, client);
        await using var context = NewContext();
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => service.GetHistoricalDailyRangeAsync(
                "ZZZXQ", new DateTime(2019, 5, 10), new DateTime(2019, 5, 14)));
            Assert.Empty(await context.MarketDataCoverage.ToListAsync());
            Assert.Empty(await context.MarketCandles.ToListAsync());
        }
        Assert.Equal(2, calls);
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(2, handler.MetadataRequestCount);
    }

    [Theory]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Invalid input"}}}""")]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Not Found","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""")]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}""")]
    [InlineData(400, """{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557360000, endDate = 1557878400"}}}""")]
    [InlineData(401, "{}")]
    [InlineData(403, "{}")]
    [InlineData(429, "{}")]
    [InlineData(500, "{}")]
    [InlineData(404, """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""")]
    [InlineData(200, "not json")]
    [InlineData(200, "{}")]
    [InlineData(0, "network")]
    public async Task Service_UnconfirmedBadRequestDoesNotCreateCoverageAndRemainsRetryable(int status, string json)
    {
        using var handler = new FailedConfirmationHandler((HttpStatusCode)status, json);
        using var client = new HttpClient(handler);
        var calls = 0;
        var provider = new YahooMarketDataProvider((_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("Yahoo Finance Server Error BadRequest");
        }, client);
        await using var context = NewContext();
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.GetHistoricalDailyRangeAsync(
                "PLTR", new DateTime(2019, 5, 10), new DateTime(2019, 5, 14)));
            Assert.Empty(await context.MarketDataCoverage.ToListAsync());
            Assert.Empty(await context.MarketCandles.ToListAsync());
        }
        Assert.Equal(2, calls);
        Assert.True(handler.RequestCount >= 2);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("authentication")]
    [InlineData("rate")]
    [InlineData("server")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    public async Task Service_UnclassifiedProviderFailuresDoNotCreateCoverageAndRemainRetryable(string failure)
    {
        Exception expected = failure switch
        {
            "network" => new HttpRequestException("Network unavailable"),
            "authentication" => new InvalidOperationException("Yahoo Finance Authentication Error"),
            "rate" => new HttpRequestException("Rate limited", null, HttpStatusCode.TooManyRequests),
            "server" => new HttpRequestException("Server error", null, HttpStatusCode.InternalServerError),
            "malformed" => new JsonException("Malformed response"),
            _ => new InvalidOperationException("Unknown provider failure")
        };
        var calls = 0;
        var provider = new YahooMarketDataProvider((_, _, _) =>
        {
            calls++;
            throw expected;
        });
        await using var context = NewContext();
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var actual = await Assert.ThrowsAnyAsync<Exception>(() =>
                service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
            Assert.Same(expected, actual);
        }
        Assert.Equal(2, calls);
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
        Assert.Empty(await context.MarketCandles.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_FailedPersistenceCanRetryWithoutDiscardingUnrelatedTrackedChanges(bool failCommit)
    {
        IInterceptor interceptor = failCommit
            ? new FailFirstCommitInterceptor()
            : new FailFirstSaveInterceptor();
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptor)
            .Options;
        await using var context = new TradingSimulatorDbContext(options);
        var unrelated = new MarketCandleEntity
        {
            Symbol = "MSFT", Source = Source, PriceAdjustmentMode = Mode, Date = Start,
            Open = 100m, High = 101m, Low = 99m, Close = 100m, Volume = 1000m,
            DownloadedAt = DateTime.UtcNow
        };
        context.MarketCandles.Add(unrelated);
        var provider = new FakeProvider { Candles = [Candle(0)] };
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));

        Assert.Equal(EntityState.Added, context.Entry(unrelated).State);
        Assert.Equal(new object[] { unrelated },
            context.ChangeTracker.Entries<MarketCandleEntity>().Select(x => x.Entity));
        Assert.Empty(context.ChangeTracker.Entries<MarketDataCoverage>());
        await using (var readContext = NewContext())
        {
            Assert.Empty(await readContext.MarketCandles.ToListAsync());
            Assert.Empty(await readContext.MarketDataCoverage.ToListAsync());
        }

        var result = await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2));

        Assert.Single(result);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(2, await context.MarketCandles.CountAsync());
        Assert.Single(await context.MarketDataCoverage.ToListAsync());
        Assert.Equal(EntityState.Unchanged, context.Entry(unrelated).State);
        Assert.All(context.ChangeTracker.Entries(), entry => Assert.Equal(EntityState.Unchanged, entry.State));
    }

    [Fact]
    public async Task Repository_OverlappingUpsertPreservesPreviouslyCoveredEmptyDates()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        await repository.UpsertAsync("AAPL", Source, Mode, Start.AddDays(1), Start.AddDays(2), [Candle(1)]);
        await repository.UpsertAsync("AAPL", Source, Mode, Start, Start.AddDays(3),
            [Candle(0), Candle(1, 200), Candle(2), Candle(3)]);
        var result = await repository.GetRangeAsync("AAPL", Source, Mode, Start, Start.AddDays(3));
        Assert.Equal(new[] { Start, Start.AddDays(1), Start.AddDays(3) }, result.Select(x => x.Date));
        Assert.All(result, x => Assert.Equal(100m, x.Close));
    }

    [Fact]
    public async Task Service_DelayedResponseUsesWinningCommittedSnapshotIncludingEmptyDates()
    {
        await using var context = NewContext();
        var provider = new FakeProvider
        {
            Candles = [Candle(0, 200), Candle(1, 200), Candle(2, 200)],
            BeforeReturn = async () =>
            {
                await using var otherContext = NewContext();
                await new MarketCandleRepository(otherContext).UpsertAsync(
                    "AAPL", Source, Mode, Start, Start.AddDays(2), [Candle(0)]);
            }
        };
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        var result = await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2));
        var winner = Assert.Single(result);
        Assert.Equal(Start, winner.Date);
        Assert.Equal(100m, winner.Close);
        Assert.Single(await context.MarketCandles.ToListAsync());
    }

    [Fact]
    public async Task Service_CommitBetweenInitialReadsDoesNotCacheStaleCandles()
    {
        await using var context = NewContext();
        var repository = new CountingRepository(new MarketCandleRepository(context))
        {
            BeforeCoverage = async () =>
            {
                await using var otherContext = NewContext();
                await new MarketCandleRepository(otherContext).UpsertAsync(
                    "AAPL", Source, Mode, Start, Start.AddDays(2), [Candle(0)]);
            }
        };
        var provider = new FakeProvider();
        var service = new BacktestHistoricalMarketDataService(provider, repository);
        var result = await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2));
        Assert.Equal(100m, Assert.Single(result).Close);
        Assert.Empty(provider.Requests);
        var reads = repository.ReadCount;
        Assert.Single(await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Equal(reads, repository.ReadCount);
    }

    [Fact]
    public async Task Service_ConcurrentExactRangeRequestsUseMemoryWithoutFurtherDatabaseReads()
    {
        await using var context = NewContext();
        var repository = new CountingRepository(new MarketCandleRepository(context));
        var provider = new FakeProvider { Candles = [Candle(0), Candle(1), Candle(2)] };
        var service = new BacktestHistoricalMarketDataService(provider, repository);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(3))));
        var reads = repository.ReadCount;
        var result = await service.GetHistoricalDailyUpToAsync("aapl", Start, Start.AddDays(3), Start.AddDays(1));
        Assert.Equal(2, result.Count);
        Assert.Single(provider.Requests);
        Assert.Equal(reads, repository.ReadCount);
    }

    [Fact]
    public async Task Service_ConcurrentDifferentRangesSerializeScopedDatabaseAccess()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
        var provider = new FakeProvider { Candles = [Candle(0), Candle(1), Candle(2), Candle(3)] };
        var service = new BacktestHistoricalMarketDataService(provider, repository);
        var results = await Task.WhenAll(
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)),
            service.GetHistoricalDailyRangeAsync("AAPL", Start.AddDays(1), Start.AddDays(3)));
        Assert.All(results, x => Assert.Equal(3, x.Count));
        Assert.Equal(new[] { (Start, Start.AddDays(2)), (Start.AddDays(3), Start.AddDays(3)) }, provider.Requests);
        Assert.Equal(4, await context.MarketCandles.CountAsync());
    }

    [SqlServerFact]
    public async Task SqlServer_ConcurrentIndependentUpsertsPreserveCommittedSnapshots()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("ITS_SQLSERVER_TEST_CONNECTION_STRING");

        var connectionStringBuilder = new SqlConnectionStringBuilder(configuredConnectionString)
        {
            InitialCatalog = $"MarketCandleConcurrency_{Guid.NewGuid():N}"
        };
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlServer(connectionStringBuilder.ConnectionString)
            .Options;

        try
        {
            await using (var setupContext = new TradingSimulatorDbContext(options))
                await setupContext.Database.EnsureCreatedAsync();

            async Task Upsert(string symbol, decimal close)
            {
                await using var context = new TradingSimulatorDbContext(options);
                await new MarketCandleRepository(context).UpsertAsync(
                    symbol, Source, Mode, Start, Start.AddDays(2), [Candle(0, close)]);
            }

            await Task.WhenAll(Upsert("AAPL", 100m), Upsert("MSFT", 100m));
            await Task.WhenAll(Upsert("NVDA", 200m), Upsert("NVDA", 300m));

            await using var verifyContext = new TradingSimulatorDbContext(options);
            var candles = await verifyContext.MarketCandles.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync();
            Assert.Collection(candles,
                x => Assert.Equal("AAPL", x.Symbol),
                x => Assert.Equal("MSFT", x.Symbol),
                x =>
                {
                    Assert.Equal("NVDA", x.Symbol);
                    Assert.Contains(x.Close, new[] { 200m, 300m });
                });
            Assert.All(candles, x => Assert.Equal(Start, x.Date));
            var coverage = await verifyContext.MarketDataCoverage.AsNoTracking().OrderBy(x => x.Symbol).ToListAsync();
            Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, coverage.Select(x => x.Symbol));
            Assert.All(coverage, x =>
            {
                Assert.Equal(Start, x.From);
                Assert.Equal(Start.AddDays(2), x.To);
            });
        }
        finally
        {
            await using var cleanupContext = new TradingSimulatorDbContext(options);
            await cleanupContext.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Service_FailedLoadCanRetryInSameScope_WithoutCertifyingInvalidResponse()
    {
        await using var context = NewContext();
        var provider = new FakeProvider { Fail = true };
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Equal(2, provider.Requests.Count);
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
        provider.Fail = false;
        var invalid = Candle(0);
        invalid.Volume = -1;
        provider.Candles = [invalid];
        Assert.Empty(await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
        Assert.Empty(await context.MarketCandles.ToListAsync());
        provider.Candles = [Candle(0)];
        Assert.Single(await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Equal(4, provider.Requests.Count);
    }

    [Fact]
    public async Task Processor_ValidOpenSurvivesRejectedCloseWithoutPersistingOrValuingRejectedCandle()
    {
        var simulatedDate = Start.AddDays(8);
        var effectiveStart = MarketHistoryRequirements.GetEffectiveStartDate(Start, simulatedDate);
        await using var context = NewContext();
        foreach (var runId in new[] { 41 })
        {
            context.BacktestRuns.Add(new BacktestRun
            {
                Id = runId,
                Name = $"SQLite regression {runId}",
                StrategyId = "test",
                StartDate = simulatedDate,
                EndDate = simulatedDate,
                InitialCapital = 1_000m,
                Status = BacktestRunStatus.Running,
                CreatedAt = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
        var repository = new MarketCandleRepository(context);
        var priorCandles = Enumerable.Range(0, 5).Select(day => Candle(day)).ToList();
        foreach (var symbol in new[] { "BUY", "HELD" })
        {
            await repository.UpsertAsync(
                symbol, Source, Mode, effectiveStart, simulatedDate.AddDays(-1), priorCandles);
        }

        var invalidBuyCandle = Candle(8);
        invalidBuyCandle.Open = 200m;
        invalidBuyCandle.High = 201m;
        invalidBuyCandle.Low = 199m;
        invalidBuyCandle.Close = 0m;
        var invalidHeldCandle = Candle(8);
        invalidHeldCandle.Open = 130m;
        invalidHeldCandle.High = 131m;
        invalidHeldCandle.Low = 129m;
        invalidHeldCandle.Close = 0m;
        var provider = new FakeProvider();
        provider.SymbolCandles["BUY"] = [invalidBuyCandle];
        provider.SymbolCandles["HELD"] = [invalidHeldCandle];
        var historicalData = new BacktestHistoricalMarketDataService(provider, repository);
        var processor = new BacktestDayProcessor(
            historicalData,
            new MarketObservationAnalyzerFactory(),
            new PortfolioRebalancingService(),
            new PortfolioAllocationService(),
            new TradingEngine(),
            new PortfolioPerformanceService(),
            new BacktestSnapshotRepository(context),
            new FeatureSnapshotRepository(context));
        var portfolio = new Portfolio
        {
            StartingCash = 1_000m,
            Cash = 1_000m,
            Positions = [new Position
            {
                Symbol = "HELD", Quantity = 1m, AveragePrice = 100m, CurrentPrice = 120m
            }]
        };
        var executionState = new BacktestExecutionState();
        executionState.GenerateAfterClose(
            portfolio,
            simulatedDate.AddDays(-1),
            new Dictionary<string, decimal> { ["BUY"] = 100m },
            [new StockRecommendation { Symbol = "BUY", AllocationPercent = 50m }],
            [new ObservationResult { Symbol = "BUY", Score = 1m }],
            new StrategyConfiguration { UseRelativeStrength = false });
        var request = new BacktestDayRequest
        {
            BacktestRunId = 41,
            CurrentDate = simulatedDate,
            HistoricalStartDate = Start,
            ExecutionState = executionState,
            Portfolio = portfolio,
            StrategyConfiguration = new StrategyConfiguration
            {
                UseRelativeStrength = false, MaxPositionWeight = 100m
            },
            Symbols = ["BUY"]
        };

        var firstAttempt = await processor.ProcessAsync(request);

        var buy = Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Buy, buy.Action);
        Assert.Equal(simulatedDate, buy.Timestamp);
        Assert.Equal(200m, buy.Price);
        Assert.Empty(executionState.Pending);
        Assert.Empty(firstAttempt.Observations);
        Assert.Equal(120m, Assert.Single(portfolio.Positions, x => x.Symbol == "HELD").CurrentPrice);
        Assert.Empty(await context.MarketCandles.AsNoTracking()
            .Where(x => x.Date == simulatedDate && (x.Symbol == "BUY" || x.Symbol == "HELD"))
            .ToListAsync());
        var rejectedDateCoverage = await context.MarketDataCoverage.AsNoTracking()
            .Where(x => (x.Symbol == "BUY" || x.Symbol == "HELD") &&
                x.From <= simulatedDate && x.To >= simulatedDate)
            .ToListAsync();
        Assert.Empty(rejectedDateCoverage);
        Assert.Empty(await context.FeatureSnapshots.AsNoTracking().ToListAsync());

        var repairedCandle = Candle(8, 200m);
        repairedCandle.High = 206m;
        repairedCandle.Low = 199m;
        repairedCandle.Close = 205m;
        provider.SymbolCandles["BUY"] = [repairedCandle];

        await processor.ProcessAsync(request);

        Assert.Single(portfolio.Trades);
        Assert.Equal(2, provider.RequestedSymbols.Count(x => x == "BUY"));
        Assert.Single(await context.MarketCandles.AsNoTracking()
            .Where(x => x.Symbol == "BUY" && x.Date == simulatedDate)
            .ToListAsync());
        Assert.Contains(await context.MarketDataCoverage.AsNoTracking()
                .Where(x => x.Symbol == "BUY" && x.From <= simulatedDate && x.To >= simulatedDate)
                .ToListAsync(),
            x => x.From == simulatedDate && x.To == simulatedDate);
    }

    private sealed class FailedConfirmationHandler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (json == "network")
                throw new HttpRequestException("Network unavailable");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }

    private sealed class EmptyChartHandler(
        string symbol, bool preListing = false, bool badRequest = false, bool knownSymbol = true) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public int MetadataRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Query.Contains("range="))
            {
                MetadataRequestCount++;
                // PLTR's first trade: 2020-09-30 13:30 UTC.
                return Task.FromResult(knownSymbol
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new
                        {
                            chart = new
                            {
                                result = new[] { new { meta = new { symbol, firstTradeDate = 1601472600L } } },
                                error = (object?)null
                            }
                        }))
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent(
                            """{"chart":{"result":null,"error":{"code":"Not Found","description":"No data found, symbol may be delisted"}}}""")
                    });
            }
            RequestCount++;
            var periods = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
            var json = preListing
                ? JsonSerializer.Serialize(new
                {
                    chart = new
                    {
                        result = (object?)null,
                        error = new
                        {
                            code = badRequest ? "Bad Request" : "Not Found",
                            description = $"Data doesn't exist for startDate = {periods["period1"]}, endDate = {periods["period2"]}"
                        }
                    }
                })
                : JsonSerializer.Serialize(new
                {
                    chart = new
                    {
                        result = new[] { new { meta = new { symbol }, indicators = new { quote = new[] { new { } } } } },
                        error = (object?)null
                    }
                });
            return Task.FromResult(new HttpResponseMessage(
                !preListing ? HttpStatusCode.OK : badRequest ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound)
            {
                Content = new StringContent(json)
            });
        }
    }

    private sealed class FakeProvider : IMarketDataProvider
    {
        public IReadOnlyList<MarketCandle> Candles { get; set; } = [];
        public List<(DateTime From, DateTime To)> Requests { get; } = [];
        public List<string> RequestedSymbols { get; } = [];
        public Dictionary<string, IReadOnlyList<MarketCandle>> SymbolCandles { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool Fail { get; set; }
        public Func<Task>? BeforeReturn { get; init; }
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from) =>
            throw new NotSupportedException();
        public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to)
        {
            Requests.Add((from, to));
            RequestedSymbols.Add(symbol);
            await Task.Yield();
            if (Fail) throw new InvalidOperationException("Provider unavailable");
            if (BeforeReturn is not null) await BeforeReturn();
            var candles = SymbolCandles.TryGetValue(symbol, out var symbolCandles) ? symbolCandles : Candles;
            return candles.Where(x => x.Date.Date >= from.Date && x.Date.Date <= to.Date).ToList();
        }
    }

    private sealed class FailFirstCommitInterceptor : DbTransactionInterceptor
    {
        private int _attempts;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                throw new InvalidOperationException("Simulated first-commit failure.");
            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private sealed class FailFirstSaveInterceptor : SaveChangesInterceptor
    {
        private int _attempts;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
                throw new InvalidOperationException("Simulated first-save failure.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CountingRepository(IMarketCandleRepository inner) : IMarketCandleRepository
    {
        public int ReadCount { get; private set; }
        public int RangeReadCount { get; private set; }
        public Func<Task>? BeforeCoverage { get; init; }
        public Task<IReadOnlyList<MarketCandle>> GetRangeAsync(string symbol, string source, string mode, DateTime from, DateTime to)
        {
            ReadCount++;
            RangeReadCount++;
            return inner.GetRangeAsync(symbol, source, mode, from, to);
        }
        public async Task<IReadOnlyList<MarketDataCoverage>> GetCoverageAsync(string symbol, string source, string mode, DateTime from, DateTime to)
        {
            ReadCount++;
            if (BeforeCoverage is not null) await BeforeCoverage();
            return await inner.GetCoverageAsync(symbol, source, mode, from, to);
        }
        public Task UpsertAsync(string symbol, string source, string mode, DateTime from, DateTime to, IReadOnlyList<MarketCandle> candles) =>
            inner.UpsertAsync(symbol, source, mode, from, to, candles);
    }

    private sealed class RecordingProgress : IWorkflowProgress
    {
        public WorkflowProgressContext Context { get; set; } = new();
        public List<string> Messages { get; } = [];
        public void Report(string message, bool force = false) => Messages.Add(message);
        public IDisposable BeginOperation(string operation, string? symbol = null) =>
            NullWorkflowProgress.Instance.BeginOperation(operation, symbol);
    }
}
