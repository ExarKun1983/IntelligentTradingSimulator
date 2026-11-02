using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Migrations;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using System.Text.Json;

namespace IntelligentTradingSimulator.Tests;

public sealed class PaperExecutionSafetyTests
{
    internal static readonly DateOnly FirstSession = new(2024, 1, 2);

    internal static PortfolioWorkflow Workflow(TradingSimulatorDbContext context, StrategyConfiguration? strategy = null,
        TimeProvider? clock = null)
    {
        strategy ??= new StrategyConfiguration { UseRelativeStrength = false };
        var allocation = new PortfolioAllocationService();
        return new PortfolioWorkflow(
            new PortfolioRepository(context), new PortfolioSnapshotRepository(context), new PositionSnapshotRepository(context),
            new PortfolioExecutionService(strategy.MaxPositionWeight, strategy.MaxPositions), new TradingEngine(),
            new PortfolioAllocationEngine(allocation, strategy.MaxPositionWeight), new PortfolioRebalancingService(),
            new PortfolioPerformanceService(), new ConsoleReporter(allocation, new PortfolioContributionService()),
            strategy, timeProvider: clock ?? PaperWorkflowTestSupport.Clock);
    }

    internal static CompletedMarketSession Session(DateOnly? date = null, decimal price = 100m) =>
        PaperWorkflowTestSupport.Session(new Dictionary<string, decimal> { ["AAA"] = price }, date ?? FirstSession);

    internal static readonly StockRecommendation[] Recommendations =
        [new() { Symbol = "AAA", Score = 10m, AllocationPercent = 100m }];

    [Fact]
    public async Task NonTransactionalProvider_IsRejectedBeforeAnyExecutionWrites()
    {
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await Assert.ThrowsAsync<NotSupportedException>(() => Workflow(context).RunAsync(Recommendations, Session()));
        Assert.Empty(await context.Portfolios.ToListAsync());
        Assert.Empty(await context.PaperPortfolioStates.ToListAsync());
        Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
    }

    [Fact]
    public async Task RecommendedAndHeldPrices_AreValidatedBeforeAnySave()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var failure = new FailAfterSave(1);
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlite(connection).AddInterceptors(failure).Options);
        await context.Database.EnsureCreatedAsync();
        StockRecommendation[] otherRecommendations = [new() { Symbol = "BBB", Score = 10m, AllocationPercent = 100m }];
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workflow(context).RunAsync(otherRecommendations, Session()));
        Assert.Empty(await context.Portfolios.ToListAsync());
        failure.Enabled = false;
        await Workflow(context).RunAsync(Recommendations, Session());
        var before = await Fingerprint(context);
        failure.Reset(1);
        var otherSession = PaperWorkflowTestSupport.Session(
            new Dictionary<string, decimal> { ["BBB"] = 100m }, FirstSession.AddDays(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workflow(context).RunAsync(otherRecommendations, otherSession));
        Assert.Equal(before, await Fingerprint(context));
    }

    [Fact]
    public async Task UnchangedValuationOnNextSession_StillWritesOneConsistentSnapshotSet()
    {
        using var database = new PaperWorkflowTestSupport.Database();
        await using var context = new TradingSimulatorDbContext(database.Options);
        var first = await Workflow(context).RunAsync(Recommendations, Session());
        var next = await Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1)));
        Assert.Equal(first.TotalValue, next.TotalValue);
        Assert.Equal(2, await context.PortfolioSnapshots.CountAsync());
        Assert.Equal(2, await context.PositionSnapshots.CountAsync());
        Assert.Equal(2, await context.PaperPortfolioExecutions.CountAsync());
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted,
            (await Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1)))).Outcome);
        Assert.Equal(2, await context.PortfolioSnapshots.CountAsync());
        Assert.Equal(2, await context.PositionSnapshots.CountAsync());
    }

    [Fact]
    public async Task ExecutedThenRevisedPricesAndStrategy_AreDuplicate_NextExecutes_OlderCannotMutate()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(connection).Options;
        await using var context = new TradingSimulatorDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var first = await Workflow(context).RunAsync(Recommendations, Session());
        Assert.Equal(PaperExecutionOutcome.Executed, first.Outcome);
        Assert.Single(await context.PaperPortfolioStates.ToListAsync());
        Assert.Single(await context.PaperPortfolioExecutions.ToListAsync());
        Assert.Single(await context.PortfolioSnapshots.ToListAsync());
        Assert.Single(await context.PositionSnapshots.ToListAsync());
        var before = await Fingerprint(context);
        await using var secondContext = new TradingSimulatorDbContext(options);
        var changed = new StrategyConfiguration { Id = "different", StopLossPercent = 1m, MaxPositions = 1 };
        var duplicate = await Workflow(secondContext, changed).RunAsync([], Session(price: 50m));
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted, duplicate.Outcome);
        Assert.Equal(before, await Fingerprint(context));

        var next = await Workflow(secondContext).RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m));
        Assert.Equal(PaperExecutionOutcome.Executed, next.Outcome);
        Assert.Equal(150m, Assert.Single(next.Portfolio.Positions).HighestPriceSinceEntry);
        Assert.Equal(2, await context.PortfolioSnapshots.CountAsync());
        Assert.Equal(2, await context.PositionSnapshots.CountAsync());
        Assert.Equal(2, await context.PaperPortfolioExecutions.CountAsync());
        before = await Fingerprint(context);
        var older = await Workflow(secondContext, changed).RunAsync([], Session(price: 1m));
        Assert.Equal(PaperExecutionOutcome.OlderSession, older.Outcome);
        Assert.Equal(before, await Fingerprint(context));
    }

    [Fact]
    public async Task LegacyPortfolio_IsNotAdoptedFromWallClockOrLatestId()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        context.Portfolios.Add(new Portfolio { Cash = 100m, StartingCash = 100m });
        await context.SaveChangesAsync();
        var before = await Fingerprint(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(context).RunAsync(Recommendations, Session()));
        Assert.Equal(before, await Fingerprint(context));
        Assert.Empty(await context.PaperPortfolioStates.ToListAsync());
    }

    [Fact]
    public async Task AdoptedUnknownHistory_RefusesCutoffAndEarlier_ThenExecutesOnlyForward()
    {
        using var database = new PaperWorkflowTestSupport.Database();
        await using var context = new TradingSimulatorDbContext(database.Options);
        var id = await SeedLegacyPortfolio(context);
        var dataBefore = await PortfolioDataFingerprint(context);
        context.PaperPortfolioStates.Add(Adoption(id));
        await context.SaveChangesAsync();
        Assert.Equal(dataBefore, await PortfolioDataFingerprint(context));
        Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
        var before = await Fingerprint(context);
        foreach (var date in new[] { FirstSession.AddDays(-1), FirstSession })
        {
            Assert.Equal(PaperExecutionOutcome.PreAdoptionSession,
                (await Workflow(context).RunAsync([], Session(date, 1m))).Outcome);
            Assert.Equal(before, await Fingerprint(context));
        }

        var next = FirstSession.AddDays(1);
        var first = await Workflow(context).RunAsync(Recommendations, Session(next));
        Assert.Equal(PaperExecutionOutcome.Executed, first.Outcome);
        Assert.Equal(id, first.Portfolio.Id);
        Assert.Equal(next, (await context.PaperPortfolioStates.SingleAsync()).LastCompletedSession);
        Assert.Equal(next, (await context.PaperPortfolioExecutions.SingleAsync()).SessionDate);
        before = await Fingerprint(context);
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted,
            (await Workflow(context).RunAsync([], Session(next, 1m))).Outcome);
        Assert.Equal(before, await Fingerprint(context));
        Assert.Equal(PaperExecutionOutcome.Executed,
            (await Workflow(context).RunAsync(Recommendations, Session(next.AddDays(1)))).Outcome);
        before = await Fingerprint(context);
        Assert.Equal(PaperExecutionOutcome.OlderSession,
            (await Workflow(context).RunAsync([], Session(next, 1m))).Outcome);
        Assert.Equal(PaperExecutionOutcome.PreAdoptionSession,
            (await Workflow(context).RunAsync([], Session())).Outcome);
        Assert.Equal(before, await Fingerprint(context));
        Assert.Equal(2, await context.PaperPortfolioExecutions.CountAsync());
        Assert.Equal(3, await context.PortfolioSnapshots.CountAsync());
        var state = await context.PaperPortfolioStates.SingleAsync();
        Assert.Equal(FirstSession, state.AdoptionCutoffSession);
        Assert.Equal("Offline test: prior session history unknown", state.AdoptionAuditReference);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public async Task FirstAdoptedExecution_RollsBackWithoutLosingBoundary_AndCanRetry(int step, bool cancel)
    {
        using var database = new PaperWorkflowTestSupport.Database();
        using var cancellation = new CancellationTokenSource();
        var failure = new FailAfterSave(0, cancel ? cancellation : null) { Enabled = false };
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlite(database.Connection).AddInterceptors(failure).Options);
        context.PaperPortfolioStates.Add(Adoption(await SeedLegacyPortfolio(context)));
        await context.SaveChangesAsync();
        var before = await Fingerprint(context);
        failure.Reset(step);
        var workflow = Workflow(context);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                workflow.RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m), cancellation.Token));
        else
            await Assert.ThrowsAsync<InjectedFailure>(() =>
                workflow.RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m)));
        Assert.Equal(before, await Fingerprint(context));
        failure.Enabled = false;
        Assert.Equal(PaperExecutionOutcome.Executed,
            (await workflow.RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m))).Outcome);
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted,
            (await workflow.RunAsync([], Session(FirstSession.AddDays(1)))).Outcome);
    }

    [Fact]
    public async Task AdoptedExecution_StillRequiresCompleteFreshPrices_AndVerifiedInitializationStillWorks()
    {
        using var database = new PaperWorkflowTestSupport.Database();
        await using var context = new TradingSimulatorDbContext(database.Options);
        var id = await SeedLegacyPortfolio(context);
        context.PaperPortfolioStates.Add(Adoption(id));
        await context.SaveChangesAsync();
        var before = await Fingerprint(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(context, clock: new LaterClock())
            .RunAsync(Recommendations, Session(FirstSession.AddDays(1))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Workflow(context).RunAsync(Recommendations,
            PaperWorkflowTestSupport.Session(new Dictionary<string, decimal> { ["BBB"] = 100m }, FirstSession.AddDays(1))));
        Assert.Equal(before, await Fingerprint(context));

        context.PaperPortfolioStates.Remove(await context.PaperPortfolioStates.SingleAsync());
        await context.SaveChangesAsync();
        context.PaperPortfolioStates.Add(new PaperPortfolioState { PortfolioId = id, LastCompletedSession = FirstSession });
        context.PaperPortfolioExecutions.Add(new PaperPortfolioExecution
            { SessionDate = FirstSession, CompletedAtUtc = PaperWorkflowTestSupport.Clock.GetUtcNow().UtcDateTime });
        await context.SaveChangesAsync();
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted,
            (await Workflow(context).RunAsync([], Session())).Outcome);
        Assert.Equal(PaperExecutionOutcome.Executed,
            (await Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1)))).Outcome);
        Assert.Null((await context.PaperPortfolioStates.SingleAsync()).AdoptionCutoffSession);
    }

    internal static PaperPortfolioState Adoption(int portfolioId) => new()
    {
        PortfolioId = portfolioId,
        AdoptionCutoffSession = FirstSession,
        AdoptionAuditReference = "Offline test: prior session history unknown",
        AdoptedAtUtc = PaperWorkflowTestSupport.Clock.GetUtcNow().UtcDateTime
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task AdoptionConstraints_RefuseMissingOrInconsistentStateWithoutChangingLegacyData(int invalidField)
    {
        using var database = new PaperWorkflowTestSupport.Database();
        await using var context = new TradingSimulatorDbContext(database.Options);
        var state = Adoption(await SeedLegacyPortfolio(context));
        var before = await Fingerprint(context);
        switch (invalidField)
        {
            case 0: state.PortfolioId = 0; break;
            case 1: state.AdoptionCutoffSession = null; break;
            case 2: state.AdoptionAuditReference = null; break;
            case 3: state.AdoptedAtUtc = null; break;
            case 4: state.AdoptionAuditReference = "   "; break;
            case 5: state.AdoptionCutoffSession = DateOnly.MinValue; break;
            case 6: state.AdoptionCutoffSession = DateOnly.MaxValue; break;
            case 7: state.LastCompletedSession = FirstSession; break;
            case 8: state.LastCompletedSession = FirstSession.AddDays(-1); break;
        }
        context.PaperPortfolioStates.Add(state);
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(before, await Fingerprint(context));
    }

    internal static async Task<int> SeedLegacyPortfolio(TradingSimulatorDbContext context)
    {
        var portfolio = new Portfolio
        {
            Name = "Explicit legacy selection", Cash = 1000m, StartingCash = 1100m,
            Positions = [new Position { Symbol = "AAA", Quantity = 1m, AveragePrice = 100m, CurrentPrice = 100m }],
            Trades = [new Trade { Symbol = "AAA", Quantity = 1m, Price = 100m,
                Timestamp = new DateTime(2020, 1, 1), Action = Core.Enums.TradeActionType.Buy }]
        };
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync();
        context.Portfolios.Add(new Portfolio { Name = "Do not silently select latest", Cash = 987m, StartingCash = 987m });
        context.PortfolioSnapshots.Add(new PortfolioSnapshot
            { PortfolioId = portfolio.Id, Timestamp = new DateTime(2020, 1, 1), Cash = 1000m, PortfolioValue = 1100m });
        context.PositionSnapshots.Add(new PositionSnapshot
            { PortfolioId = portfolio.Id, Timestamp = new DateTime(2020, 1, 1), Symbol = "AAA", Quantity = 1m, CurrentPrice = 100m });
        await context.SaveChangesAsync();
        return portfolio.Id;
    }

    [Fact]
    public async Task FreshnessIsRecheckedBeforeMutation_ButCommittedDuplicatesRemainExplicit()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var session = Session();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Workflow(context, clock: new LaterClock()).RunAsync(Recommendations, session));
        Assert.Empty(await context.Portfolios.ToListAsync());
        await Workflow(context).RunAsync(Recommendations, session);
        var before = await Fingerprint(context);
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted,
            (await Workflow(context, clock: new LaterClock()).RunAsync([], session)).Outcome);
        Assert.Equal(before, await Fingerprint(context));
    }

    [Fact]
    public async Task MappedPortfolio_DoesNotChangeWhenANewerUnmappedPortfolioExists()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var first = await Workflow(context).RunAsync(Recommendations, Session());
        var other = new Portfolio { Name = "Not main", Cash = 123m, StartingCash = 123m };
        context.Portfolios.Add(other);
        await context.SaveChangesAsync();
        var next = await Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m));
        Assert.Equal(first.Portfolio.Id, next.Portfolio.Id);
        Assert.NotEqual(other.Id, next.Portfolio.Id);
        Assert.Equal(123m, (await context.Portfolios.AsNoTracking().SingleAsync(x => x.Id == other.Id)).Cash);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    public async Task EveryPersistenceBoundary_RollsBackInitializationAndExecution_AndSameContextCanRetry(int step, bool cancel)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        var failure = new FailAfterSave(step, cancel ? cancellation : null);
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlite(connection).AddInterceptors(failure).Options);
        await context.Database.EnsureCreatedAsync();
        await AssertRollbackAndRetry(context, failure, cancellation, cancel);
    }

    internal static async Task AssertRollbackAndRetry(TradingSimulatorDbContext context, FailAfterSave failure,
        CancellationTokenSource cancellation, bool cancel)
    {
        var workflow = Workflow(context);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                workflow.RunAsync(Recommendations, Session(), cancellation.Token));
        else
            await Assert.ThrowsAsync<InjectedFailure>(() => workflow.RunAsync(Recommendations, Session(), cancellation.Token));
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(0, await context.Portfolios.CountAsync());
        Assert.Equal(0, await context.Positions.CountAsync());
        Assert.Equal(0, await context.Trades.CountAsync());
        Assert.Equal(0, await context.PortfolioSnapshots.CountAsync());
        Assert.Equal(0, await context.PositionSnapshots.CountAsync());
        Assert.Equal(0, await context.PaperPortfolioStates.CountAsync());
        Assert.Equal(0, await context.PaperPortfolioExecutions.CountAsync());
        failure.Enabled = false;
        Assert.Equal(PaperExecutionOutcome.Executed, (await workflow.RunAsync(Recommendations, Session())).Outcome);
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted, (await workflow.RunAsync(Recommendations, Session())).Outcome);
        Assert.Single(await context.Portfolios.ToListAsync());
        Assert.Equal(2, await context.Trades.CountAsync());
        Assert.Single(await context.PortfolioSnapshots.ToListAsync());
        Assert.Single(await context.PositionSnapshots.ToListAsync());
        Assert.Single(await context.PaperPortfolioExecutions.ToListAsync());
    }

    [Fact]
    public async Task FailureAfterExistingPortfolioMutation_RestoresPricesHighWaterBaselineCashAndSnapshots()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var failure = new FailAfterSave(0);
        await using var context = new TradingSimulatorDbContext(new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseSqlite(connection).AddInterceptors(failure).Options);
        await context.Database.EnsureCreatedAsync();
        await Workflow(context).RunAsync(Recommendations, Session());
        var before = await Fingerprint(context);
        failure.Reset(4);
        await Assert.ThrowsAsync<InjectedFailure>(() =>
            Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m)));
        Assert.Equal(before, await Fingerprint(context));
        failure.Enabled = false;
        Assert.Equal(PaperExecutionOutcome.Executed,
            (await Workflow(context).RunAsync(Recommendations, Session(FirstSession.AddDays(1), 150m))).Outcome);
    }

    internal static async Task<string> Fingerprint(TradingSimulatorDbContext context)
    {
        context.ChangeTracker.Clear();
        var state = await context.PaperPortfolioStates.AsNoTracking().OrderBy(x => x.Identity).ToListAsync();
        return await PortfolioDataFingerprint(context) + "|" +
            JsonSerializer.Serialize(await context.PaperPortfolioExecutions.AsNoTracking()
                .OrderBy(x => x.Identity).ThenBy(x => x.SessionDate).ToListAsync()) + "|" +
            JsonSerializer.Serialize(state.Select(x => new { x.Identity, x.PortfolioId, x.LastCompletedSession,
                x.AdoptionCutoffSession, x.AdoptionAuditReference, x.AdoptedAtUtc }));
    }

    internal static async Task<string> PortfolioDataFingerprint(TradingSimulatorDbContext context)
    {
        context.ChangeTracker.Clear();
        var portfolios = await context.Portfolios.AsNoTracking().Include(x => x.Positions).Include(x => x.Trades)
            .OrderBy(x => x.Id).ToListAsync();
        return JsonSerializer.Serialize(portfolios.Select(x => new { x.Id, x.Name, x.Cash, x.StartingCash,
            Positions = x.Positions.OrderBy(p => p.Id), Trades = x.Trades.OrderBy(t => t.Id) })) + "|" +
            JsonSerializer.Serialize(await context.PortfolioSnapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync()) + "|" +
            JsonSerializer.Serialize(await context.PositionSnapshots.AsNoTracking().OrderBy(x => x.Id).ToListAsync());
    }

    [Fact]
    public void Migration_IsAdditiveAndSnapshotMatchesModel()
    {
        using var context = new TradingSimulatorDbContext(
            new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer().Options);
        Assert.False(context.Database.HasPendingModelChanges());
        var modelConstraint = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(PaperPortfolioState))!
            .GetCheckConstraints().Single(x => x.Name == "CK_PaperPortfolioStates_Adoption").Sql;
        var migrationConstraint = Assert.IsType<AddCheckConstraintOperation>(
            Assert.Single(new AddPaperPortfolioAdoption().UpOperations.OfType<AddCheckConstraintOperation>())).Sql;
        var windowsConstraint = migrationConstraint.Replace("\n", "\r\n", StringComparison.Ordinal);
        Assert.Equal(migrationConstraint, windowsConstraint.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(migrationConstraint, modelConstraint);
        var script = context.GetService<IMigrator>().GenerateScript("20261026114621_AddPositionEffectiveTarget");
        Assert.Contains("CREATE TABLE [PaperPortfolioStates]", script);
        Assert.Contains("CREATE TABLE [PaperPortfolioExecutions]", script);
        Assert.Contains("UNIQUE INDEX [IX_PaperPortfolioStates_PortfolioId]", script);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE [Portfolios]", script, StringComparison.OrdinalIgnoreCase);
        var adoption = context.GetService<IMigrator>().GenerateScript("20261027095852_AddPaperExecutionSafety");
        Assert.Contains("ADD [AdoptionCutoffSession] date NULL", adoption);
        Assert.Contains("EXEC(N'CREATE PROCEDURE [dbo].[AdoptPaperPortfolioUnknownHistory]", adoption);
        var idempotent = context.GetService<IMigrator>().GenerateScript("20261027095852_AddPaperExecutionSafety",
            options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("EXEC(N'CREATE PROCEDURE [dbo].[AdoptPaperPortfolioUnknownHistory]", idempotent);
        Assert.Contains("CK_PaperPortfolioStates_Adoption", adoption);
        Assert.DoesNotContain("INSERT INTO dbo.PaperPortfolioExecutions", adoption);
        Assert.DoesNotContain("DROP TABLE", adoption, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UPDATE ", adoption, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class InjectedFailure : Exception;

    private sealed class LaterClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2024, 1, 15, 17, 0, 0, TimeSpan.Zero);
    }

    internal sealed class FailAfterSave(int step, CancellationTokenSource? cancellation = null) : SaveChangesInterceptor
    {
        private int _count;
        private int _step = step;
        public bool Enabled { get; set; } = true;
        public void Reset(int step) { _count = 0; _step = step; Enabled = true; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && ++_count == _step)
            {
                if (cancellation is not null)
                {
                    cancellation.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                throw new InjectedFailure();
            }
            return ValueTask.FromResult(result);
        }
    }
}
