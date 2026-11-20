using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestCommandExitCodeTests
{
    [Theory]
    [InlineData(BacktestRunStatus.Completed, 0)]
    [InlineData(BacktestRunStatus.Failed, 1)]
    public void RunBacktestCommand_MapsEngineStatusToCommandExitCode(
        BacktestRunStatus status,
        int expectedExitCode)
    {
        Assert.Equal(expectedExitCode, BacktestCommandExitCode.ForBacktestResult(status));
    }

    [Fact]
    public void RunVariantsCommand_ReturnsFailureWhenAnyVariantFails()
    {
        Assert.Equal(1, BacktestCommandExitCode.ForVariantResults(allSucceeded: false));
    }

    [Fact]
    public void RunVariantsCommand_ReturnsSuccessWhenAllVariantsSucceed()
    {
        Assert.Equal(0, BacktestCommandExitCode.ForVariantResults(allSucceeded: true));
    }
}
