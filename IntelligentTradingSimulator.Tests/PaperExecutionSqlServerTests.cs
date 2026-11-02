using System.Data.Common;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace IntelligentTradingSimulator.Tests;

public sealed class SqlServerPaperFactAttribute : FactAttribute
{
    public SqlServerPaperFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPER_TEST_SQL_CONNECTION")))
            Skip = "Opt-in isolated SQL Server tests: set PAPER_TEST_SQL_CONNECTION to a master connection on a disposable test server.";
    }
}

public sealed class SqlServerPaperTheoryAttribute : TheoryAttribute
{
    public SqlServerPaperTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAPER_TEST_SQL_CONNECTION")))
            Skip = "Opt-in isolated SQL Server tests: set PAPER_TEST_SQL_CONNECTION to a master connection on a disposable test server.";
    }
}

public sealed class PaperExecutionSqlServerTests
{
    [SqlServerPaperTheory]
    [InlineData(2, 2, false)]
    [InlineData(2, 3, false)]
    [InlineData(3, 2, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 2, true)]
    public async Task IndependentConnections_SerializeInitializationAndDifferentSessions(int firstDay, int secondDay, bool existing)
    {
        await using var database = await IsolatedDatabase.Create();
        if (existing)
        {
            await using var seed = database.Context();
            await PaperExecutionSafetyTests.Workflow(seed).RunAsync(PaperExecutionSafetyTests.Recommendations,
                PaperExecutionSafetyTests.Session(new DateOnly(2024, 1, 1)));
        }
        var gate = new HoldFirstSave();
        var attempted = new ObserveLock();
        await using var firstContext = database.Context(gate);
        await using var secondContext = database.Context(attempted);
        Assert.NotSame(firstContext.Database.GetDbConnection(), secondContext.Database.GetDbConnection());
        var first = PaperExecutionSafetyTests.Workflow(firstContext).RunAsync(PaperExecutionSafetyTests.Recommendations,
            PaperExecutionSafetyTests.Session(new DateOnly(2024, 1, firstDay)));
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var second = PaperExecutionSafetyTests.Workflow(secondContext,
                new StrategyConfiguration { Id = "changed-strategy", UseRelativeStrength = false })
                .RunAsync(PaperExecutionSafetyTests.Recommendations,
                    PaperExecutionSafetyTests.Session(new DateOnly(2024, 1, secondDay), 150m));
            await attempted.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(second.IsCompleted);
            gate.Release.TrySetResult();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(90));
            Assert.Equal(PaperExecutionOutcome.Executed, results[0].Outcome);
            Assert.Equal(secondDay == firstDay ? PaperExecutionOutcome.AlreadyExecuted :
                secondDay < firstDay ? PaperExecutionOutcome.OlderSession : PaperExecutionOutcome.Executed, results[1].Outcome);
        }
        finally
        {
            gate.Release.TrySetResult();
            await first;
        }
        await using var read = database.Context();
        var expected = (existing ? 1 : 0) + (secondDay > firstDay ? 2 : 1);
        Assert.Equal(1, await read.Portfolios.CountAsync());
        Assert.Equal(1, await read.PaperPortfolioStates.CountAsync());
        Assert.Equal(expected, await read.PaperPortfolioExecutions.CountAsync());
        Assert.Equal(expected, await read.PortfolioSnapshots.CountAsync());
        Assert.Equal(expected, await read.PositionSnapshots.CountAsync());
        Assert.Equal(new DateOnly(2024, 1, Math.Max(firstDay, secondDay)),
            (await read.PaperPortfolioStates.SingleAsync()).LastCompletedSession);
    }

    [SqlServerPaperFact]
    public async Task ConcurrentPartiallyOverlappingCollectionsWithSameStrategy_ReachPaperExecutionGuard()
    {
        await using var database = await IsolatedDatabase.Create();
        var barrier = new FeatureSnapshotReadBarrier();
        await using var firstContext = database.Context(barrier);
        await using var secondContext = database.Context(barrier);
        var strategy = new StrategyConfiguration { Id = "concurrent", UseRelativeStrength = false };

        var firstCollection = CreateMarketDataWorkflow(firstContext, strategy)
            .CollectAndAnalyzeCompletedSessionAsync(["AAA", "BBB"], new DateTime(2024, 1, 1));
        var secondCollection = CreateMarketDataWorkflow(secondContext, strategy)
            .CollectAndAnalyzeCompletedSessionAsync(["AAA", "CCC"], new DateTime(2024, 1, 1));
        var collected = await Task.WhenAll(firstCollection, secondCollection)
            .WaitAsync(TimeSpan.FromSeconds(90));
        Assert.All(collected, result => Assert.NotNull(result.ExecutionSession));

        var firstExecution = PaperExecutionSafetyTests.Workflow(
            firstContext, strategy, PaperWorkflowTestSupport.Clock)
            .RunAsync(collected[0].Recommendations, collected[0].ExecutionSession!);
        var secondExecution = PaperExecutionSafetyTests.Workflow(
            secondContext, strategy, PaperWorkflowTestSupport.Clock)
            .RunAsync(collected[1].Recommendations, collected[1].ExecutionSession!);
        var outcomes = await Task.WhenAll(firstExecution, secondExecution)
            .WaitAsync(TimeSpan.FromSeconds(90));

        Assert.Contains(outcomes, result => result.Outcome == PaperExecutionOutcome.Executed);
        Assert.Contains(outcomes, result => result.Outcome == PaperExecutionOutcome.AlreadyExecuted);
        await using var read = database.Context();
        Assert.Equal(3, await read.FeatureSnapshots.CountAsync());
        Assert.Single(await read.PaperPortfolioExecutions.ToListAsync());
    }

    [SqlServerPaperFact]
    public async Task SqlConstraints_RejectDuplicateSessionAndSecondStableMapping()
    {
        await using var database = await IsolatedDatabase.Create();
        await using var context = database.Context();
        await PaperExecutionSafetyTests.Workflow(context).RunAsync(PaperExecutionSafetyTests.Recommendations,
            PaperExecutionSafetyTests.Session());
        var portfolioId = (await context.PaperPortfolioStates.SingleAsync()).PortfolioId;
        var duplicateSession = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PaperPortfolioExecutions ([Identity], SessionDate, CompletedAtUtc)
            VALUES (N'main', {PaperExecutionSafetyTests.FirstSession}, SYSUTCDATETIME());
            """));
        Assert.Contains(duplicateSession.Number, new[] { 2601, 2627 });
        var duplicateMapping = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PaperPortfolioStates ([Identity], PortfolioId, LastCompletedSession)
            VALUES (N'other', {portfolioId}, {PaperExecutionSafetyTests.FirstSession});
            """));
        Assert.Contains(duplicateMapping.Number, new[] { 2601, 2627 });
        Assert.Single(await context.PaperPortfolioStates.ToListAsync());
        Assert.Single(await context.PaperPortfolioExecutions.ToListAsync());
    }

    [SqlServerPaperFact]
    public async Task NegativeApplicationLockResult_FailsClosed_AndRetryAfterReleaseSucceeds()
    {
        await using var database = await IsolatedDatabase.Create();
        await using var holder = database.Context();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'PaperPortfolio:main',
                @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 0;
            IF @result < 0 THROW 51002, 'Test could not acquire lock.', 1;
            """);
        await using var contender = database.Context(new ImmediateLockTimeout());
        var workflow = PaperExecutionSafetyTests.Workflow(contender);
        var exception = await Assert.ThrowsAsync<SqlException>(() =>
            workflow.RunAsync(PaperExecutionSafetyTests.Recommendations, PaperExecutionSafetyTests.Session()));
        Assert.Equal(51001, exception.Number);
        Assert.Empty(contender.ChangeTracker.Entries());
        Assert.Equal(0, await contender.Portfolios.CountAsync());
        Assert.Equal(0, await contender.PaperPortfolioExecutions.CountAsync());
        await transaction.RollbackAsync();
        Assert.Equal(PaperExecutionOutcome.Executed,
            (await workflow.RunAsync(PaperExecutionSafetyTests.Recommendations, PaperExecutionSafetyTests.Session())).Outcome);
    }

    [SqlServerPaperTheory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(4, true)]
    [InlineData(7, true)]
    public async Task ActualSqlTransaction_RollsBackAtEveryPersistenceStep_AndAllowsRetry(int step, bool cancel)
    {
        await using var database = await IsolatedDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var failure = new PaperExecutionSafetyTests.FailAfterSave(step, cancel ? cancellation : null);
        await using var context = database.Context(failure);
        await PaperExecutionSafetyTests.AssertRollbackAndRetry(context, failure, cancellation, cancel);
    }

    [SqlServerPaperFact]
    public async Task Upgrade_PreservesExistingPortfolioIds_AndRequiresExplicitInitialization()
    {
        await using var database = await IsolatedDatabase.Create("20261026114621_AddPositionEffectiveTarget");
        await using var context = database.Context();
        var portfolio = new Portfolio { Name = "Legacy", StartingCash = 100m, Cash = 100m };
        context.Portfolios.Add(portfolio);
        await context.SaveChangesAsync();
        var id = portfolio.Id;
        await context.Database.MigrateAsync();
        Assert.Equal(id, (await context.Portfolios.AsNoTracking().SingleAsync()).Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PaperExecutionSafetyTests.Workflow(context).RunAsync(PaperExecutionSafetyTests.Recommendations,
                PaperExecutionSafetyTests.Session()));
        Assert.Empty(await context.PaperPortfolioStates.ToListAsync());
        Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
    }

    [SqlServerPaperTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptionUpgrade_PreservesEveryLegacyRow_AndVerifiedMappings(bool verified)
    {
        await using var database = await IsolatedDatabase.Create("20261027095852_AddPaperExecutionSafety");
        await using var context = database.Context();
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(context);
        if (verified)
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO PaperPortfolioStates ([Identity], PortfolioId, LastCompletedSession)
                    VALUES (N'main', {id}, {PaperExecutionSafetyTests.FirstSession});
                INSERT INTO PaperPortfolioExecutions ([Identity], SessionDate, CompletedAtUtc)
                    VALUES (N'main', {PaperExecutionSafetyTests.FirstSession}, SYSUTCDATETIME());
                """);
        }
        var before = await PaperExecutionSafetyTests.PortfolioDataFingerprint(context);
        await context.Database.MigrateAsync();
        Assert.Equal(before, await PaperExecutionSafetyTests.PortfolioDataFingerprint(context));
        if (verified)
        {
            var failure = await Assert.ThrowsAsync<SqlException>(() => Adopt(context, id));
            Assert.Equal(51024, failure.Number);
            Assert.Null((await context.PaperPortfolioStates.SingleAsync()).AdoptionCutoffSession);
            Assert.Single(await context.PaperPortfolioExecutions.ToListAsync());
        }
        else
        {
            await Adopt(context, id);
            Assert.Equal(before, await PaperExecutionSafetyTests.PortfolioDataFingerprint(context));
            var state = await context.PaperPortfolioStates.AsNoTracking().SingleAsync();
            Assert.Equal(id, state.PortfolioId);
            Assert.Null(state.LastCompletedSession);
            Assert.Equal(PaperExecutionSafetyTests.FirstSession, state.AdoptionCutoffSession);
            Assert.Equal("Isolated test: session history unknown", state.AdoptionAuditReference);
            Assert.NotNull(state.AdoptedAtUtc);
            Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
        }
        var guarded = await PaperExecutionSafetyTests.Fingerprint(context);
        foreach (var date in new[] { PaperExecutionSafetyTests.FirstSession.AddDays(-1), PaperExecutionSafetyTests.FirstSession })
        {
            var outcome = (await PaperExecutionSafetyTests.Workflow(context).RunAsync([],
                PaperExecutionSafetyTests.Session(date, 1m))).Outcome;
            Assert.Equal(verified
                ? (date == PaperExecutionSafetyTests.FirstSession ? PaperExecutionOutcome.AlreadyExecuted : PaperExecutionOutcome.OlderSession)
                : PaperExecutionOutcome.PreAdoptionSession, outcome);
            Assert.Equal(guarded, await PaperExecutionSafetyTests.Fingerprint(context));
        }
        var next = PaperExecutionSafetyTests.FirstSession.AddDays(1);
        Assert.Equal(PaperExecutionOutcome.Executed, (await PaperExecutionSafetyTests.Workflow(context)
            .RunAsync(PaperExecutionSafetyTests.Recommendations, PaperExecutionSafetyTests.Session(next))).Outcome);
        Assert.Equal(id, (await context.PaperPortfolioStates.SingleAsync()).PortfolioId);
        guarded = await PaperExecutionSafetyTests.Fingerprint(context);
        Assert.Equal(PaperExecutionOutcome.AlreadyExecuted, (await PaperExecutionSafetyTests.Workflow(context)
            .RunAsync([], PaperExecutionSafetyTests.Session(next, 1m))).Outcome);
        Assert.Equal(guarded, await PaperExecutionSafetyTests.Fingerprint(context));
        Assert.Equal(PaperExecutionOutcome.Executed, (await PaperExecutionSafetyTests.Workflow(context)
            .RunAsync(PaperExecutionSafetyTests.Recommendations, PaperExecutionSafetyTests.Session(next.AddDays(1)))).Outcome);
        guarded = await PaperExecutionSafetyTests.Fingerprint(context);
        Assert.Equal(PaperExecutionOutcome.OlderSession, (await PaperExecutionSafetyTests.Workflow(context)
            .RunAsync([], PaperExecutionSafetyTests.Session(next, 1m))).Outcome);
        Assert.Equal(guarded, await PaperExecutionSafetyTests.Fingerprint(context));
    }

    [SqlServerPaperFact]
    public async Task Adoption_InvalidInputsBacktestsAndConflicts_FailWithoutChanges()
    {
        await using var database = await IsolatedDatabase.Create();
        await using var context = database.Context();
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(context);
        var catalog = context.Database.GetDbConnection().Database;
        var before = await PaperExecutionSafetyTests.Fingerprint(context);
        (string? Database, int? Id, DateOnly? Cutoff, string? Audit)[] invalid =
        [
            (null, id, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog + "_wrong", id, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog, null, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog, 0, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog, -1, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog, int.MaxValue, PaperExecutionSafetyTests.FirstSession, "unknown history"),
            (catalog, id, null, "unknown history"),
            (catalog, id, DateOnly.MinValue, "unknown history"),
            (catalog, id, DateOnly.MaxValue, "unknown history"),
            (catalog, id, PaperExecutionSafetyTests.FirstSession, null),
            (catalog, id, PaperExecutionSafetyTests.FirstSession, ""),
            (catalog, id, PaperExecutionSafetyTests.FirstSession, " \t\r\n "),
            (catalog, id, PaperExecutionSafetyTests.FirstSession, "\v\f\u0085\u00a0\u1680\u2000\u2028\u202f\u205f\u3000"),
            (catalog, id, PaperExecutionSafetyTests.FirstSession, new string('x', 1001))
        ];
        foreach (var input in invalid)
        {
            await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                EXEC dbo.AdoptPaperPortfolioUnknownHistory @ExpectedDatabase={input.Database},
                    @PortfolioId={input.Id}, @CutoffSession={input.Cutoff}, @AuditReference={input.Audit};
                """));
            Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        }
        var backtest = new BacktestRun { PortfolioId = id };
        context.BacktestRuns.Add(backtest);
        await context.SaveChangesAsync();
        Assert.Equal(51024, (await Assert.ThrowsAsync<SqlException>(() => Adopt(context, id))).Number);
        Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        context.BacktestRuns.Remove(await context.BacktestRuns.SingleAsync());
        await context.SaveChangesAsync();
        context.PaperPortfolioStates.Add(new PaperPortfolioState
            { Identity = "other", PortfolioId = id, LastCompletedSession = PaperExecutionSafetyTests.FirstSession });
        await context.SaveChangesAsync();
        before = await PaperExecutionSafetyTests.Fingerprint(context);
        Assert.Equal(51024, (await Assert.ThrowsAsync<SqlException>(() => Adopt(context, id))).Number);
        Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        context.PaperPortfolioStates.Remove(await context.PaperPortfolioStates.SingleAsync());
        await context.SaveChangesAsync();
        await Adopt(context, id);
        before = await PaperExecutionSafetyTests.Fingerprint(context);
        foreach (var cutoff in new[] { PaperExecutionSafetyTests.FirstSession.AddDays(-1), PaperExecutionSafetyTests.FirstSession,
            PaperExecutionSafetyTests.FirstSession.AddDays(1) })
        {
            Assert.Equal(51024, (await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                EXEC dbo.AdoptPaperPortfolioUnknownHistory @ExpectedDatabase={catalog},
                    @PortfolioId={id}, @CutoffSession={cutoff}, @AuditReference=N'different decision';
                """))).Number);
            Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        }
    }

    [SqlServerPaperFact]
    public async Task AdoptionConstraints_RejectPartialAuditAndCompletionAtOrBeforeCutoff()
    {
        await using var database = await IsolatedDatabase.Create();
        await using var context = database.Context();
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(context);
        var before = await PaperExecutionSafetyTests.Fingerprint(context);
        var invalid = await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO PaperPortfolioStates ([Identity], PortfolioId, AdoptionCutoffSession)
                VALUES (N'main', {id}, {PaperExecutionSafetyTests.FirstSession});
            """));
        Assert.Equal(547, invalid.Number);
        Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        await Adopt(context, id);
        before = await PaperExecutionSafetyTests.Fingerprint(context);
        foreach (var date in new[] { PaperExecutionSafetyTests.FirstSession.AddDays(-1), PaperExecutionSafetyTests.FirstSession })
        {
            Assert.Equal(547, (await Assert.ThrowsAsync<SqlException>(() => context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE PaperPortfolioStates SET LastCompletedSession={date} WHERE [Identity]=N'main';
                """))).Number);
            Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        }
    }

    [SqlServerPaperTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndependentConnections_SerializeAdoptionAgainstExecutionAndAdoption(bool competingAdoption)
    {
        await using var database = await IsolatedDatabase.Create();
        await using var first = database.Context();
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(first);
        await using var gate = database.Context();
        await using var transaction = await gate.Database.BeginTransactionAsync();
        await HoldAdoptionGate(gate);
        var reached = await InstallAdoptionGate(first);
        var adoption = Adopt(first, id);
        var observed = new ObserveLock();
        await using var second = database.Context(observed);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // This signal is emitted inside the INSERT trigger: adoption owns the main lock.
            if (competingAdoption)
            {
                var contender = Adopt(second, id);
                await observed.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(contender.IsCompleted);
                await transaction.RollbackAsync();
                await adoption;
                Assert.Equal(51024, (await Assert.ThrowsAsync<SqlException>(() => contender)).Number);
            }
            else
            {
                var contender = PaperExecutionSafetyTests.Workflow(second).RunAsync(
                    PaperExecutionSafetyTests.Recommendations,
                    PaperExecutionSafetyTests.Session(PaperExecutionSafetyTests.FirstSession.AddDays(1)));
                await observed.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.False(contender.IsCompleted);
                await transaction.RollbackAsync();
                await adoption;
                Assert.Equal(PaperExecutionOutcome.Executed, (await contender).Outcome);
            }
        }
        finally
        {
            if (transaction.GetDbTransaction().Connection is not null)
                await transaction.RollbackAsync();
            await adoption;
        }
        await using var read = database.Context();
        Assert.Equal(id, (await read.PaperPortfolioStates.SingleAsync()).PortfolioId);
        Assert.Equal(competingAdoption ? 0 : 1, await read.PaperPortfolioExecutions.CountAsync());
    }

    [SqlServerPaperTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adoption_FailureOrCancellationAfterInsert_RollsBackAndCanRetry(bool cancel)
    {
        await using var database = await IsolatedDatabase.Create();
        await using var context = database.Context();
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(context);
        var before = await PaperExecutionSafetyTests.Fingerprint(context);
        if (cancel)
        {
            await using var gate = database.Context();
            await using var transaction = await gate.Database.BeginTransactionAsync();
            await HoldAdoptionGate(gate);
            var reached = await InstallAdoptionGate(context);
            using var cancellation = new CancellationTokenSource();
            var adoption = Adopt(context, id, cancellation.Token);
            try
            {
                await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adoption);
            }
            finally
            {
                cancellation.Cancel();
                await transaction.RollbackAsync();
            }
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER dbo.AdoptionTestGate ON dbo.PaperPortfolioStates AFTER INSERT AS
                THROW 51999, 'Injected adoption failure after insert.', 1;
                """);
            Assert.Equal(51999, (await Assert.ThrowsAsync<SqlException>(() => Adopt(context, id))).Number);
        }
        Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.AdoptionTestGate;");
        await Adopt(context, id);
        Assert.Equal(id, (await context.PaperPortfolioStates.SingleAsync()).PortfolioId);
        Assert.Empty(await context.PaperPortfolioExecutions.ToListAsync());
    }

    [SqlServerPaperTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdoptedExecution_ActualSqlRollback_PreservesBoundaryAndAllowsRetry(bool cancel)
    {
        await using var database = await IsolatedDatabase.Create();
        using var cancellation = new CancellationTokenSource();
        var failure = new PaperExecutionSafetyTests.FailAfterSave(0, cancel ? cancellation : null) { Enabled = false };
        await using var context = database.Context(failure);
        var id = await PaperExecutionSafetyTests.SeedLegacyPortfolio(context);
        await Adopt(context, id);
        var before = await PaperExecutionSafetyTests.Fingerprint(context);
        failure.Reset(5);
        var workflow = PaperExecutionSafetyTests.Workflow(context);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(
                PaperExecutionSafetyTests.Recommendations,
                PaperExecutionSafetyTests.Session(PaperExecutionSafetyTests.FirstSession.AddDays(1), 150m), cancellation.Token));
        else
            await Assert.ThrowsAsync<PaperExecutionSafetyTests.InjectedFailure>(() => workflow.RunAsync(
                PaperExecutionSafetyTests.Recommendations,
                PaperExecutionSafetyTests.Session(PaperExecutionSafetyTests.FirstSession.AddDays(1), 150m)));
        Assert.Equal(before, await PaperExecutionSafetyTests.Fingerprint(context));
        failure.Enabled = false;
        Assert.Equal(PaperExecutionOutcome.Executed, (await workflow.RunAsync(PaperExecutionSafetyTests.Recommendations,
            PaperExecutionSafetyTests.Session(PaperExecutionSafetyTests.FirstSession.AddDays(1), 150m))).Outcome);
    }

    private static Task<int> Adopt(TradingSimulatorDbContext context, int id, CancellationToken cancellationToken = default)
    {
        var catalog = context.Database.GetDbConnection().Database;
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            EXEC dbo.AdoptPaperPortfolioUnknownHistory @ExpectedDatabase={catalog},
                @PortfolioId={id}, @CutoffSession={PaperExecutionSafetyTests.FirstSession},
                @AuditReference=N'Isolated test: session history unknown';
            """, cancellationToken);
    }

    private static Task<int> HoldAdoptionGate(TradingSimulatorDbContext context) => context.Database.ExecuteSqlRawAsync("""
        DECLARE @result int;
        EXEC @result = sys.sp_getapplock @Resource=N'ITS_AdoptionTestGate',
            @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=0;
        IF @result < 0 THROW 51998, 'Could not hold test gate.', 1;
        """);

    private static async Task<TaskCompletionSource> InstallAdoptionGate(TradingSimulatorDbContext context)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ((SqlConnection)context.Database.GetDbConnection()).InfoMessage += (_, message) =>
        {
            if (message.Message.Contains("Adoption test gate reached", StringComparison.Ordinal))
                reached.TrySetResult();
        };
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER dbo.AdoptionTestGate ON dbo.PaperPortfolioStates AFTER INSERT AS
            BEGIN
                RAISERROR ('Adoption test gate reached', 10, 1) WITH NOWAIT;
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=N'ITS_AdoptionTestGate',
                    @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=60000;
                IF @result < 0 THROW 51998, 'Adoption test gate unavailable.', 1;
            END;
            """);
        return reached;
    }

    private static MarketDataWorkflow CreateMarketDataWorkflow(
        TradingSimulatorDbContext context,
        StrategyConfiguration strategy) =>
        new(new ConcurrentMarketDataProvider(), new MarketObservationAnalyzer(strategy),
            new PortfolioRecommendationService(), new ObservationResultRepository(context),
            new FeatureSnapshotRepository(context), strategy, timeProvider: PaperWorkflowTestSupport.Clock);

    private sealed class ConcurrentMarketDataProvider : IMarketDataProvider
    {
        private static readonly IReadOnlyList<MarketCandle> Candles =
            Enumerable.Range(0, 5).Select(index =>
            {
                var close = 100m + index;
                return new MarketCandle
                {
                    Date = new DateTime(2024, 1, 1).AddDays(index),
                    Open = close,
                    High = close + 1m,
                    Low = close - 1m,
                    Close = close,
                    Volume = 1_000m
                };
            }).ToArray();

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from) =>
            Task.FromResult(Candles);

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(
            string symbol,
            DateTime from,
            DateTime to) => Task.FromResult(Candles);
    }

    private sealed class FeatureSnapshotReadBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothQueriesExecuted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _queryCount;

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("[FeatureSnapshots]", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var count = Interlocked.Increment(ref _queryCount);
                if (count <= 2)
                {
                    if (count == 2)
                        _bothQueriesExecuted.TrySetResult();
                    await _bothQueriesExecuted.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }

            return result;
        }
    }

    private sealed class HoldFirstSave : SaveChangesInterceptor
    {
        private int _count;
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _count) == 1)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class ObserveLock : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sys.sp_getapplock", StringComparison.Ordinal)
                || command.CommandText.Contains("EXEC dbo.AdoptPaperPortfolioUnknownHistory", StringComparison.Ordinal))
                Reached.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ImmediateLockTimeout : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("sys.sp_getapplock", StringComparison.Ordinal))
                command.CommandText = command.CommandText.Replace("@LockTimeout = 60000", "@LockTimeout = 0", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class IsolatedDatabase : IAsyncDisposable
    {
        private readonly string _connectionString;
        private IsolatedDatabase(string connectionString) => _connectionString = connectionString;

        public TradingSimulatorDbContext Context(params IInterceptor[] interceptors) =>
            new(new DbContextOptionsBuilder<TradingSimulatorDbContext>().UseSqlServer(_connectionString)
                .AddInterceptors(interceptors).Options);

        public static async Task<IsolatedDatabase> Create(string? targetMigration = null)
        {
            var builder = new SqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("PAPER_TEST_SQL_CONNECTION")
                ?? throw new InvalidOperationException("SQL Server tests require explicit opt-in."));
            if (!string.Equals(builder.InitialCatalog, "master", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PAPER_TEST_SQL_CONNECTION must explicitly target master on a disposable test server.");
            builder.InitialCatalog = "ITS_PaperTests_" + Guid.NewGuid().ToString("N");
            builder.Pooling = false;
            var database = new IsolatedDatabase(builder.ConnectionString);
            try
            {
                await using var context = database.Context();
                await context.GetService<IMigrator>().MigrateAsync(targetMigration);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Only a generated, private test catalog can ever reach this destructive cleanup.
            var catalog = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            if (!catalog.StartsWith("ITS_PaperTests_", StringComparison.Ordinal) || catalog.Length != 47)
                throw new InvalidOperationException("Refusing to delete a non-test database.");
            await using var context = Context();
            await context.Database.EnsureDeletedAsync();
        }
    }
}
