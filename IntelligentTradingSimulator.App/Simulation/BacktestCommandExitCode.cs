using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.App.Simulation;

public static class BacktestCommandExitCode
{
    public static int ForBacktestResult(BacktestRunStatus status) =>
        status == BacktestRunStatus.Failed ? 1 : 0;

    public static int ForVariantResults(bool allSucceeded) =>
        allSucceeded ? 0 : 1;
}
