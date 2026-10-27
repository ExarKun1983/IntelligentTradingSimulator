using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class PortfolioRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public PortfolioRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<Portfolio>> GetAllAsync()
    {
        return await _dbContext.Portfolios
            .Where(x => !_dbContext.BacktestRuns.Any(run => run.PortfolioId == x.Id))
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<Portfolio?> GetLatestAsync()
    {
        return await _dbContext.Portfolios
            .Where(x => !_dbContext.BacktestRuns.Any(run => run.PortfolioId == x.Id))
            .Include(x => x.Positions)
            .Include(x => x.Trades)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();
    }

    public async Task SaveAsync(
    Portfolio portfolio,
    CancellationToken cancellationToken = default)
    {
        if (!TradingPrecisionPolicy.CashBalanceIsSupported(portfolio.Cash)
            || !TradingPrecisionPolicy.CashBalanceIsSupported(portfolio.StartingCash))
        {
            throw new ArgumentOutOfRangeException(nameof(portfolio),
                "Cash and starting cash must be within the supported cash range and exactly representable at eight decimal places.");
        }

        if (portfolio.Id == 0)
        {
            _dbContext.Portfolios.Add(portfolio);
        }
        else
        {
            _dbContext.Portfolios.Update(portfolio);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<(PaperExecutionOutcome Outcome, T? Result, Portfolio Portfolio)> ExecutePaperSessionAsync<T>(
        DateOnly sessionDate,
        Action<Portfolio> validate,
        Func<Portfolio, bool, CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken = default)
    {
        if (!_dbContext.Database.IsRelational())
            throw new NotSupportedException("Paper execution requires a transactional relational database.");
        await using var transaction =
            await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (_dbContext.Database.IsSqlServer())
            {
                // The resource deliberately excludes strategy, session and portfolio database ID.
                await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    DECLARE @result int;
                    EXEC @result = sys.sp_getapplock
                        @Resource = {"PaperPortfolio:main"}, @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction', @LockTimeout = 60000;
                    IF @result < 0 THROW 51001, 'Could not acquire the paper portfolio lock.', 1;
                    """, cancellationToken);
            }

            // A reused context must not make decisions from a previously loaded portfolio.
            _dbContext.ChangeTracker.Clear();
            var state = await _dbContext.PaperPortfolioStates
                .Include(x => x.Portfolio).ThenInclude(x => x.Positions)
                .Include(x => x.Portfolio).ThenInclude(x => x.Trades)
                .SingleOrDefaultAsync(x => x.Identity == PaperPortfolioState.MainIdentity, cancellationToken);
            var isNew = state is null;
            if (state is null)
            {
                if (await _dbContext.Portfolios.AnyAsync(
                    x => !_dbContext.BacktestRuns.Any(run => run.PortfolioId == x.Id), cancellationToken))
                    throw new InvalidOperationException(
                        "Existing paper portfolios require explicit verified session initialization. See docs/runbook.md.");
                state = new PaperPortfolioState
                {
                    Portfolio = new Portfolio { Name = "Main Portfolio", StartingCash = 10000m, Cash = 10000m }
                };
            }

            if (state.LastCompletedSession is { } last && sessionDate <= last)
            {
                var outcome = sessionDate == last ? PaperExecutionOutcome.AlreadyExecuted : PaperExecutionOutcome.OlderSession;
                return (outcome, default, state.Portfolio);
            }
            if (!isNew && state.LastCompletedSession is null)
                throw new InvalidOperationException("Paper execution state requires verified session initialization.");
            if (await _dbContext.PaperPortfolioExecutions.AnyAsync(
                x => x.Identity == PaperPortfolioState.MainIdentity && x.SessionDate == sessionDate, cancellationToken))
                throw new InvalidOperationException("Paper session state is inconsistent; execution refused.");

            validate(state.Portfolio);
            if (isNew)
            {
                _dbContext.PaperPortfolioStates.Add(state);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            var result = await execute(state.Portfolio, isNew, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            state.LastCompletedSession = sessionDate;
            _dbContext.PaperPortfolioExecutions.Add(new PaperPortfolioExecution
            {
                SessionDate = sessionDate, CompletedAtUtc = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (PaperExecutionOutcome.Executed, result, state.Portfolio);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            finally
            {
                _dbContext.ChangeTracker.Clear();
            }
            throw;
        }
    }
}