using System;
using System.IO;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class ConsoleReporterTests
{
    [Fact]
    public void PrintBacktestSummary_IncludesMaxDrawdown()
    {
        var reporter = new ConsoleReporter(
            new PortfolioAllocationService(),
            new PortfolioContributionService());

        var result = new BacktestSummaryReportResult
        {
            IsSuccess = true,
            BacktestRunId = 7,
            RunName = "Issue 13",
            StartCapital = 10_000m,
            FinalCapital = 9_500m,
            Profit = -500m,
            ReturnPercent = -5m,
            MaxDrawdownPercent = 12.34m,
            TradesCount = 4,
            RebalancesCount = 2
        };

        using var writer = new StringWriter();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(writer);
            reporter.PrintBacktestSummary(result);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        Assert.Contains("Max Drawdown:   12.34%", writer.ToString());
    }
}
