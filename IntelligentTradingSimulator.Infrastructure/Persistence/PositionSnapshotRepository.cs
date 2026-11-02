using System.Collections.Generic;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class PositionSnapshotRepository
{
    private readonly TradingSimulatorDbContext _dbContext;

    public PositionSnapshotRepository(
        TradingSimulatorDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task SaveAsync(
        IEnumerable<PositionSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        _dbContext.PositionSnapshots
            .AddRange(snapshots);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
