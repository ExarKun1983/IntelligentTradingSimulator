using System.Threading.Tasks;

namespace IntelligentTradingSimulator.App.Simulation;

public interface IBacktestEngine
{
    Task<BacktestEngineResult> RunAsync(BacktestEngineRequest request);
}
