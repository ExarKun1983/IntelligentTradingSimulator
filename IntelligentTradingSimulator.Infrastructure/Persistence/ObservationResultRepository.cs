using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class ObservationResultRepository
    : IObservationResultRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public ObservationResultRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveAsync(
    IReadOnlyList<ObservationResult> results)
    {
        if (!results.Any())
        {
            return;
        }

        var run =
            new ObservationRun
            {
                CreatedAt = DateTime.UtcNow
            };

        var rankedResults =
            results
                .OrderByDescending(x => x.Score)
                .Select((x, index) =>
                    new ObservationResultEntity
                    {
                        Symbol = x.Symbol,

                        MomentumPercent =
                            x.MomentumPercent,

                        VolumeTrendPercent =
                            x.VolumeTrendPercent,

                        VolatilityPercent =
                            x.VolatilityPercent,

                        IsAboveSma5 =
                            x.IsAboveSma5,

                        Score =
                            x.Score,

                        RankPosition =
                            index + 1
                    })
                .ToList();

        run.Results.AddRange(
            rankedResults);

        _dbContext.ObservationRuns.Add(
            run);

        await _dbContext.SaveChangesAsync();
    }

    public async Task<ObservationRun?> GetLatestRunAsync()
    {
        return await _dbContext.ObservationRuns
            .Include(x => x.Results)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
    }

    public async Task<ObservationRun?> GetPreviousRunAsync()
    {
        return await _dbContext.ObservationRuns
            .Include(x => x.Results)
            .OrderByDescending(x => x.CreatedAt)
            .Skip(1)
            .FirstOrDefaultAsync();
    }
}