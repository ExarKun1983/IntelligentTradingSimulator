using System.Threading.Tasks;

using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IBacktestRunRepository
{
    Task AddAsync(BacktestRun run);

    Task<BacktestRun?> GetByIdAsync(int id);

    Task UpdateAsync(BacktestRun run);
}
