using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class PortfolioSnapshotRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public PortfolioSnapshotRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveAsync(
        PortfolioSnapshot snapshot)
    {

        var lastSnapshot =
        await _dbContext.PortfolioSnapshots
            .Where(x => x.PortfolioId == snapshot.PortfolioId)
            .OrderByDescending(x => x.Timestamp)
            .FirstOrDefaultAsync();

        if (lastSnapshot is not null &&
            Math.Round(lastSnapshot.PortfolioValue, 2) ==
            Math.Round(snapshot.PortfolioValue, 2))
        {
            return;
        }


        _dbContext.PortfolioSnapshots.Add(
            snapshot);

        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<PortfolioSnapshot>>
    GetByPortfolioAsync(
        int portfolioId)
    {
        return await _dbContext
            .PortfolioSnapshots
            .Where(x => x.PortfolioId == portfolioId)
            .OrderBy(x => x.Timestamp)
            .ToListAsync();
    }
}
