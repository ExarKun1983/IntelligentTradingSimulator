using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace IntelligentTradingSimulator.Tests;

internal static class PaperWorkflowTestSupport
{
    public static readonly TimeProvider Clock = new FixedClock();

    public static CompletedMarketSession Session(IReadOnlyDictionary<string, decimal> prices, DateOnly date) =>
        CompletedMarketSession.Create(prices.Keys, prices.ToDictionary(x => x.Key,
            x => (IReadOnlyList<MarketCandle>)[new MarketCandle
            {
                Date = date.ToDateTime(TimeOnly.MinValue), Open = x.Value, High = x.Value,
                Low = x.Value, Close = x.Value, Volume = 100
            }]), Clock.GetUtcNow());

    public static async Task<PortfolioState> RunAsync(PortfolioWorkflow workflow, TradingSimulatorDbContext context,
        IReadOnlyList<StockRecommendation> recommendations, IReadOnlyDictionary<string, decimal> prices)
    {
        var state = await context.PaperPortfolioStates.SingleOrDefaultAsync();
        if (state is null)
        {
            var portfolio = await new PortfolioRepository(context).GetLatestAsync();
            if (portfolio is not null)
            {
                // This fixture knows the seed's prior session; production never infers it.
                state = new PaperPortfolioState { Portfolio = portfolio, LastCompletedSession = new DateOnly(2024, 1, 1) };
                context.PaperPortfolioStates.Add(state);
                await context.SaveChangesAsync();
            }
        }
        var date = (state?.LastCompletedSession ?? new DateOnly(2024, 1, 1)).AddDays(1);
        return await workflow.RunAsync(recommendations, Session(prices, date));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2024, 1, 8, 17, 0, 0, TimeSpan.Zero);
    }

    internal sealed class Database : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public DbContextOptions<TradingSimulatorDbContext> Options { get; }
        public Database()
        {
            Connection.Open();
            Options = new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(Connection).Options;
            using var context = new TradingSimulatorDbContext(Options);
            context.Database.EnsureCreated();
        }
        public void Dispose() => Connection.Dispose();
    }
}
