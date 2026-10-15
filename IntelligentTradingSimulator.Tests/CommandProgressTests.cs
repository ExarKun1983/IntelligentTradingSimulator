using System.Diagnostics;
using IntelligentTradingSimulator.App.Simulation;

namespace IntelligentTradingSimulator.Tests;

public sealed class CommandProgressTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCommand_DisabledProgressPreservesErrorWithoutSuccess(bool disabled)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(BacktestEngine).Assembly.Location);
        start.ArgumentList.Add($"--tuning-input={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.json")}");
        if (disabled)
            start.ArgumentList.Add("--NO-PROGRESS");

        using var process = Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;

        Assert.True(process.ExitCode == 1, error);
        Assert.Contains("Tuning analysis failed:", output);
        Assert.DoesNotContain("Workflow completed", output);
        if (disabled)
        {
            Assert.DoesNotContain("Initializing application", output);
            Assert.DoesNotContain("Workflow failed", output);
        }
        else
        {
            Assert.Contains("Initializing application", output);
            Assert.Contains("Workflow failed", output);
        }
    }
}
