using System;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyTuningCommandLineParser
{
    public static bool TryParse(string[] args, out string? inputPath, out string? outputPath, out string? error)
    {
        inputPath = null;
        outputPath = null;
        error = null;
        foreach (var option in new[] { "--tuning-input", "--export-tuning-json" })
        {
            var matches = Array.FindAll(args, arg => arg.Equals(option, StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase));
            if (matches.Length == 0)
                continue;
            if (matches.Length != 1 || matches[0].Length <= option.Length + 1 ||
                string.IsNullOrWhiteSpace(matches[0][(option.Length + 1)..]))
            {
                error = $"Error: specify {option}=<path> exactly once with a non-empty path.";
                return false;
            }
            if (option == "--tuning-input")
                inputPath = matches[0][(option.Length + 1)..];
            else
                outputPath = matches[0][(option.Length + 1)..];
        }
        if (outputPath is not null && inputPath is null)
            error = "Error: --export-tuning-json requires --tuning-input.";
        if (inputPath is not null && Array.Exists(args, arg =>
                arg.Equals("--run-backtest", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--run-variants", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--backtest-run-id", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--export-backtest-csv", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--export-backtest-snapshots-csv", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--export-comparison-csv", StringComparison.OrdinalIgnoreCase)))
            error = "Error: tuning is an offline research mode and cannot be combined with backtest/report modes.";
        return error is null;
    }
}
