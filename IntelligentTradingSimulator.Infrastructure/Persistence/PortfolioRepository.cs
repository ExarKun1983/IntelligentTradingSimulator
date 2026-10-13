using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

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
    Portfolio portfolio)
    {
        if (portfolio.Id == 0)
        {
            _dbContext.Portfolios.Add(portfolio);
        }
        else
        {
            _dbContext.Portfolios.Update(portfolio);
        }

        await _dbContext.SaveChangesAsync();
    }
}