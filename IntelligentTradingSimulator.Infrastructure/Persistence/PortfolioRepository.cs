using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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
            .OrderBy(x => x.Id)
            .ToListAsync();
    }

    public async Task<Portfolio?> GetLatestAsync()
    {
        return await _dbContext.Portfolios
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