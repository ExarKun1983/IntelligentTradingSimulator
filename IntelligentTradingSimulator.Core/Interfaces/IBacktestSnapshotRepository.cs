using System.Collections.Generic;
using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IBacktestSnapshotRepository
{
    Task AddAsync(BacktestSnapshot snapshot);

    Task AddRangeAsync(IReadOnlyList<BacktestSnapshot> snapshots);

    Task<List<BacktestSnapshot>> GetByRunIdAsync(int backtestRunId);
}
