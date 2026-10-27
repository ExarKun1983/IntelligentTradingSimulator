using System.Data.Common;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

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
            if (command.CommandText.Contains("sys.sp_getapplock", StringComparison.Ordinal))
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
