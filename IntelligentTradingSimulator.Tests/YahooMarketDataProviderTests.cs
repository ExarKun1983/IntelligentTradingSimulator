using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.MarketData;

namespace IntelligentTradingSimulator.Tests;

public sealed class YahooMarketDataProviderTests
{
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
}
