using System;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.App.Configuration;

public static class StrategyComparisonCommandLineParser
{
    public static bool TryParse(
        string[] args,
        out StrategyComparisonSortMetric? sortBy,
        out string? benchmarkSymbol,
        out string? errorMessage)
    {
        sortBy = null;
        benchmarkSymbol = null;
        errorMessage = null;

        var sortArg = Array.Find(
            args,
            a => a.StartsWith("--comparison-sort-by=", StringComparison.OrdinalIgnoreCase));
        if (sortArg is not null)
        {
            var metricName = sortArg["--comparison-sort-by=".Length..];
            if (!Enum.TryParse<StrategyComparisonSortMetric>(metricName, true, out var metric) ||
                !Enum.IsDefined(metric) || !Array.Exists(Enum.GetNames<StrategyComparisonSortMetric>(),
                    name => name.Equals(metricName, StringComparison.OrdinalIgnoreCase)))
            {
                errorMessage = "Error: --comparison-sort-by must be ReturnPercent, MaxDrawdownPercent, TradesCount, AlphaPercent, or RelativeReturnPercent.";
                return false;
            }
            sortBy = metric;
        }

        var benchmarkArg = Array.Find(
            args,
            a => a.StartsWith("--comparison-benchmark=", StringComparison.OrdinalIgnoreCase));
        benchmarkSymbol = benchmarkArg?["--comparison-benchmark=".Length..].Trim();
        if (sortBy is StrategyComparisonSortMetric.AlphaPercent or StrategyComparisonSortMetric.RelativeReturnPercent &&
            string.IsNullOrWhiteSpace(benchmarkSymbol))
        {
            errorMessage = "Error: benchmark sorting requires --comparison-benchmark=<symbol>.";
            return false;
        }

        return true;
    }
}
