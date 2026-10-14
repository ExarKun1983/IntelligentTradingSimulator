using System;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyComparisonCsvCommandLineParser
{
    public static bool TryParse(string[] args, out string? filePath, out string? errorMessage)
    {
        filePath = null;
        errorMessage = null;
        const string option = "--export-comparison-csv";
        var exportArgs = Array.FindAll(args, a =>
            a.Equals(option, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

        if (exportArgs.Length == 0)
            return true;

        if (exportArgs.Length != 1 || exportArgs[0].Length <= option.Length + 1 ||
            string.IsNullOrWhiteSpace(exportArgs[0][(option.Length + 1)..]))
        {
            errorMessage = "Error: specify --export-comparison-csv=<path> exactly once with a non-empty path.";
            return false;
        }

        if (!Array.Exists(args, a => a.Equals("--run-variants", StringComparison.OrdinalIgnoreCase)) ||
            Array.Exists(args, a =>
                a.Equals("--run-backtest", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--backtest-run-id", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = "Error: comparison CSV export requires --run-variants without --run-backtest or --backtest-run-id.";
            return false;
        }

        filePath = exportArgs[0][(option.Length + 1)..];
        return true;
    }
}
