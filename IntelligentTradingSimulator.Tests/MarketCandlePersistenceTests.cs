using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

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
        var nextService = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(nextContext), progress);
        var overlap = await nextService.GetHistoricalDailyUpToAsync("AAPL", Start.AddDays(1), Start.AddDays(5), Start.AddDays(2));
        Assert.Equal(100m, Assert.Single(overlap).Close);
        Assert.Single(provider.Requests);
        Assert.Contains(progress.Messages, x => x.EndsWith("| database"));
    }

    [Fact]
    public async Task Service_FetchesOnlyLeadingInternalAndTrailingGaps()
    {
        await using var context = NewContext();
        var repository = new MarketCandleRepository(context);
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
    public async Task Service_FailedLoadCanBeRetried_WithoutCertifyingInvalidResponse()
    {
        await using var context = NewContext();
        var provider = new FakeProvider { Fail = true };
        var service = new BacktestHistoricalMarketDataService(provider, new MarketCandleRepository(context));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
        provider.Fail = false;
        var invalid = Candle(0);
        invalid.Volume = -1;
        provider.Candles = [invalid];
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Empty(await context.MarketDataCoverage.ToListAsync());
        provider.Candles = [Candle(0)];
        Assert.Single(await service.GetHistoricalDailyRangeAsync("AAPL", Start, Start.AddDays(2)));
        Assert.Equal(3, provider.Requests.Count);
    }

    private sealed class FakeProvider : IMarketDataProvider
    {
        public IReadOnlyList<MarketCandle> Candles { get; set; } = [];
        public List<(DateTime From, DateTime To)> Requests { get; } = [];
        public bool Fail { get; set; }
        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from) =>
            throw new NotSupportedException();
        public async Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to)
        {
            Requests.Add((from, to));
            await Task.Yield();
            if (Fail) throw new InvalidOperationException("Provider unavailable");
            return Candles;
        }
    }

    private sealed class CountingRepository(IMarketCandleRepository inner) : IMarketCandleRepository
    {
        public int ReadCount { get; private set; }
        public Task<IReadOnlyList<MarketCandle>> GetRangeAsync(string symbol, string source, string mode, DateTime from, DateTime to)
        {
            ReadCount++;
            return inner.GetRangeAsync(symbol, source, mode, from, to);
        }
        public Task<IReadOnlyList<MarketDataCoverage>> GetCoverageAsync(string symbol, string source, string mode, DateTime from, DateTime to)
        {
            ReadCount++;
            return inner.GetCoverageAsync(symbol, source, mode, from, to);
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
