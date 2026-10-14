using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class BacktestRunRepository : IBacktestRunRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public BacktestRunRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(BacktestRun run)
    {
        _dbContext.BacktestRuns.Add(run);

        await _dbContext.SaveChangesAsync();
    }

    public Task<bool> ExistsAsync(int id)
    {
        return _dbContext.BacktestRuns.AnyAsync(x => x.Id == id);
    }

    public async Task<BacktestRun?> GetByIdAsync(int id)
    {
        return await _dbContext.BacktestRuns
            .Include(x => x.Snapshots)
            .Include(x => x.Portfolio)
                .ThenInclude(x => x!.Trades)
            .Include(x => x.Portfolio)
                .ThenInclude(x => x!.Positions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task UpdateAsync(BacktestRun run)
    {
        _dbContext.BacktestRuns.Update(run);

        await _dbContext.SaveChangesAsync();
    }

    public async Task MarkFailedAsync(int runId)
    {
        _dbContext.ChangeTracker.Clear();

        var run = await _dbContext.BacktestRuns
            .FirstOrDefaultAsync(x => x.Id == runId);

        if (run is null)
            return;

        run.Status = Core.Enums.BacktestRunStatus.Failed;
        await _dbContext.SaveChangesAsync();
    }
}
