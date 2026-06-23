using System;
using System.IO;
using IntelligentTradingSimulator.App.Reporting;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class ConsoleReporterBacktestSummaryTests
{
    [Fact]
    public void PrintBacktestSummary_PrintsTradesAndRebalances()
    {
        var reporter =
            new ConsoleReporter(
                new PortfolioAllocationService(),
                new PortfolioContributionService());

        var result = new BacktestSummaryReportResult
        {
            RunId = 42,
            Report = new BacktestSummaryReport
            {
                RunId = 42,
                Name = "Backtest 42",
                StartDate = new DateTime(2024, 1, 1),
                EndDate = new DateTime(2024, 1, 5),
                InitialCapital = 10_000m,
                FinalCapital = 10_750m,
                TotalProfit = 750m,
                TotalReturnPercent = 7.5m,
                TradesCount = 3,
                RebalancesCount = 2
            }
        };

        using var output = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(output);

        try
        {
            reporter.PrintBacktestSummary(result);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var consoleOutput = output.ToString();

        Assert.Contains("===== BACKTEST SUMMARY =====", consoleOutput);
        Assert.Contains("Trades: 3", consoleOutput);
        Assert.Contains("Rebalances: 2", consoleOutput);
    }
}
