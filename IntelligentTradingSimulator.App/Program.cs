using System;
using IntelligentTradingSimulator.App.Reporting;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var configuration =
    new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

var connectionString =
    configuration.GetConnectionString("TradingDb")
    ?? throw new InvalidOperationException(
        "Connection string 'TradingDb' is not configured.");

var services = new ServiceCollection();

services.AddInfrastructure(connectionString);

services.AddSingleton<MarketObservationAnalyzer>();
services.AddSingleton<PortfolioRecommendationService>();
services.AddSingleton<PortfolioExecutionService>();
services.AddSingleton<TradingEngine>();
services.AddSingleton<PortfolioPerformanceService>();
services.AddSingleton<PortfolioAllocationService>();
services.AddSingleton<PortfolioAllocationEngine>();
services.AddSingleton<PortfolioContributionService>();
services.AddSingleton<PortfolioRebalancingService>();

services.AddSingleton<ConsoleReporter>();
services.AddTransient<BacktestSummaryReportService>();
services.AddScoped<MarketDataWorkflow>();
services.AddScoped<PortfolioWorkflow>();
services.AddScoped<SimulationRunner>();

await using var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();

var runner = scope.ServiceProvider.GetRequiredService<SimulationRunner>();
var backtestSummaryReportService =
    scope.ServiceProvider.GetRequiredService<BacktestSummaryReportService>();
var reporter = scope.ServiceProvider.GetRequiredService<ConsoleReporter>();

await runner.RunAsync();

if (TryGetBacktestRunId(args, out var backtestRunId))
{
    var summaryResult =
        await backtestSummaryReportService.GenerateAsync(backtestRunId);

    reporter.PrintBacktestSummary(summaryResult);
}

static bool TryGetBacktestRunId(string[] args, out int runId)
{
    const string argumentPrefix = "--backtest-run-id=";
    runId = 0;

    foreach (var arg in args)
    {
        if (!arg.StartsWith(argumentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        return int.TryParse(arg[argumentPrefix.Length..], out runId);
    }

    return false;
}