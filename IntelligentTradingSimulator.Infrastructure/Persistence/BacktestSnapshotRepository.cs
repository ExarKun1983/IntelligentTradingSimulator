using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class BacktestSnapshotRepository : IBacktestSnapshotRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public BacktestSnapshotRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(BacktestSnapshot snapshot)
    {
        _dbContext.BacktestSnapshots.Add(snapshot);

        await _dbContext.SaveChangesAsync();
    }

    public async Task AddRangeAsync(
        IReadOnlyList<BacktestSnapshot> snapshots)
    {
        _dbContext.BacktestSnapshots.AddRange(snapshots);

        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<BacktestSnapshot>> GetByRunIdAsync(
        int backtestRunId)
    {
        return await _dbContext.BacktestSnapshots
            .Where(x => x.BacktestRunId == backtestRunId)
            .OrderBy(x => x.Date)
            .ToListAsync();
    }
}
