using System;
using System.IO;

namespace IntelligentTradingSimulator.App.Configuration;

public static class BacktestSnapshotCsvCommandLineParser
{
    public static bool TryParse(string[] args, out string? filePath, out string? errorMessage)
    {
        filePath = null;
        errorMessage = null;
        const string option = "--export-backtest-snapshots-csv";
        var exportArgs = Array.FindAll(args, a =>
            a.Equals(option, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));

        if (exportArgs.Length == 0)
            return true;

        if (exportArgs.Length != 1 || exportArgs[0].Length <= option.Length + 1 ||
            string.IsNullOrWhiteSpace(exportArgs[0][(option.Length + 1)..]))
        {
            errorMessage = "Error: specify --export-backtest-snapshots-csv=<path> exactly once with a non-empty path.";
            return false;
        }

        var runIdArgs = Array.FindAll(args, a =>
            a.Equals("--backtest-run-id", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase));
        if (runIdArgs.Length > 0)
        {
            if (runIdArgs.Length != 1 ||
                !runIdArgs[0].StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(runIdArgs[0]["--backtest-run-id=".Length..], out var runId) || runId <= 0)
            {
                errorMessage = "Error: snapshot CSV export requires a single positive --backtest-run-id=<id>.";
                return false;
            }
        }
        else if (!Array.Exists(args, a => a.Equals("--run-backtest", StringComparison.OrdinalIgnoreCase)))
        {
            errorMessage = "Error: snapshot CSV export requires --run-backtest or --backtest-run-id=<id>.";
            return false;
        }

        var snapshotPath = exportArgs[0][(option.Length + 1)..];
        if (BacktestSummaryCsvCommandLineParser.TryParse(args, out var summaryPath, out _) &&
            summaryPath is not null)
        {
            try
            {
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                if (string.Equals(Path.GetFullPath(snapshotPath), Path.GetFullPath(summaryPath), comparison))
                {
                    errorMessage = "Error: summary and snapshot CSV exports require different output paths.";
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                errorMessage = "Error: summary and snapshot CSV exports require valid output paths.";
                return false;
            }
        }

        filePath = snapshotPath;
        return true;
    }
}
