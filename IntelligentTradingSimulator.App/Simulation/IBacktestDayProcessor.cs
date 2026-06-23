using System.Threading.Tasks;

namespace IntelligentTradingSimulator.App.Simulation;

public interface IBacktestDayProcessor
{
    Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request);
}
