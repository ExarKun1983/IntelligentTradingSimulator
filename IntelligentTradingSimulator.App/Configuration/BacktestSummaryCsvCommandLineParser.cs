using System;

namespace IntelligentTradingSimulator.App.Configuration;

public static class BacktestSummaryCsvCommandLineParser
{
    public static bool TryParse(string[] args, out string? filePath, out string? errorMessage)
    {
        filePath = null;
        errorMessage = null;
        const string option = "--export-backtest-csv";
        var exportArgs = Array.FindAll(args, a =>
            a.Equals(option, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

        if (exportArgs.Length == 0)
            return true;

        if (exportArgs.Length != 1 || exportArgs[0].Length <= option.Length + 1 ||
            string.IsNullOrWhiteSpace(exportArgs[0][(option.Length + 1)..]))
        {
            errorMessage = "Error: specify --export-backtest-csv=<path> exactly once with a non-empty path.";
            return false;
        }

        var runIdArg = Array.Find(args, a =>
            a.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase));
        if (runIdArg is not null)
        {
            if (!int.TryParse(runIdArg["--backtest-run-id=".Length..], out var runId) || runId <= 0)
            {
                errorMessage = "Error: CSV export requires a positive --backtest-run-id.";
                return false;
            }
        }
        else if (!Array.Exists(args, a => a.Equals("--run-backtest", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = "Error: CSV export requires --run-backtest or --backtest-run-id=<id>.";
            return false;
        }

        filePath = exportArgs[0][(option.Length + 1)..];
        return true;
    }
}
