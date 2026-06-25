using System;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Interfaces;
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

var maxPositions =
    configuration.GetValue<int?>("Strategy:MaxPositions")
    ?? 10;

if (maxPositions <= 0)
{
    throw new InvalidOperationException(
        "Configuration value 'Strategy:MaxPositions' must be greater than zero.");
}

var services = new ServiceCollection();

services.AddInfrastructure(connectionString);

services.AddSingleton<MarketObservationAnalyzer>();
services.AddSingleton(
    _ => new PortfolioRecommendationService(maxPositions));
services.AddSingleton<PortfolioExecutionService>();
services.AddSingleton<TradingEngine>();
services.AddSingleton<PortfolioPerformanceService>();
services.AddSingleton<PortfolioAllocationService>();
services.AddSingleton<PortfolioAllocationEngine>();
services.AddSingleton<PortfolioContributionService>();
services.AddSingleton<PortfolioRebalancingService>();

services.AddSingleton<ConsoleReporter>();
services.AddScoped<MarketDataWorkflow>();
services.AddScoped<PortfolioWorkflow>();
services.AddScoped<SimulationRunner>();
services.AddScoped<IBacktestSummaryReportService, BacktestSummaryReportService>();
services.AddScoped<BacktestHistoricalMarketDataService>();
services.AddScoped<IBenchmarkDataService, BenchmarkDataService>();
services.AddScoped<IBacktestBenchmarkService, BacktestBenchmarkService>();

await using var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();

var backtestRunIdArg = Array.Find(
    args,
    a => a.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase));

if (backtestRunIdArg is not null &&
    int.TryParse(
        backtestRunIdArg["--backtest-run-id=".Length..],
        out var backtestRunId))
{
    var reportService =
        scope.ServiceProvider.GetRequiredService<IBacktestSummaryReportService>();

    var reporter =
        scope.ServiceProvider.GetRequiredService<ConsoleReporter>();

    var result = await reportService.GenerateAsync(backtestRunId);
    reporter.PrintBacktestSummary(result);
    return;
}

var runner = scope.ServiceProvider.GetRequiredService<SimulationRunner>();

await runner.RunAsync();