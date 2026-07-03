using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
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

var selectedStrategyConfiguration =
    StrategyConfigurationLoader.Load(configuration);

var services = new ServiceCollection();

services.AddInfrastructure(connectionString);
services.AddSingleton(selectedStrategyConfiguration);
services.AddSingleton<BacktestEngineRequestFactory>();

services.AddSingleton<MarketObservationAnalyzer>();
services.AddSingleton(
    _ => new PortfolioRecommendationService(
        selectedStrategyConfiguration.MaxPositions));
services.AddSingleton(
    _ => new PortfolioExecutionService(
        selectedStrategyConfiguration.MaxPositionWeight));
services.AddSingleton<TradingEngine>();
services.AddSingleton<PortfolioPerformanceService>();
services.AddSingleton<PortfolioAllocationService>();
services.AddSingleton(
    sp => new PortfolioAllocationEngine(
        sp.GetRequiredService<PortfolioAllocationService>(),
        selectedStrategyConfiguration.MaxPositionWeight));
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
services.AddScoped<IBacktestDayProcessor, BacktestDayProcessor>();
services.AddScoped<IBacktestEngine, BacktestEngine>();
services.AddScoped<IStrategyVariantRunner, StrategyVariantRunner>();

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

var runBacktestArg = Array.Find(
    args,
    a => a.Equals("--run-backtest", StringComparison.OrdinalIgnoreCase));

if (runBacktestArg is not null)
{
    var startDateArg = Array.Find(
        args,
        a => a.StartsWith("--backtest-start=", StringComparison.OrdinalIgnoreCase));

    var endDateArg = Array.Find(
        args,
        a => a.StartsWith("--backtest-end=", StringComparison.OrdinalIgnoreCase));

    var nameArg = Array.Find(
        args,
        a => a.StartsWith("--backtest-name=", StringComparison.OrdinalIgnoreCase));

    var endDate = endDateArg is not null &&
                  DateTime.TryParse(endDateArg["--backtest-end=".Length..], out var parsedEnd)
        ? parsedEnd
        : DateTime.Today.AddDays(-1);

    var startDate = startDateArg is not null &&
                    DateTime.TryParse(startDateArg["--backtest-start=".Length..], out var parsedStart)
        ? parsedStart
        : endDate.AddYears(-1);

    var name = nameArg is not null
        ? nameArg["--backtest-name=".Length..]
        : $"Backtest {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}";

    IReadOnlyList<string> symbols = ["AAPL", "MSFT", "GOOG", "AMZN", "NVDA"];

    var requestFactory = scope.ServiceProvider.GetRequiredService<BacktestEngineRequestFactory>();
    var engine = scope.ServiceProvider.GetRequiredService<IBacktestEngine>();
    var reportService = scope.ServiceProvider.GetRequiredService<IBacktestSummaryReportService>();
    var reporter = scope.ServiceProvider.GetRequiredService<ConsoleReporter>();

    var request = requestFactory.Create(
        name: name,
        startDate: startDate,
        endDate: endDate,
        historicalStartDate: startDate.AddDays(-selectedStrategyConfiguration.LookbackDays),
        initialCapital: 100_000m,
        symbols: symbols);

    Console.WriteLine($"Starting backtest: {name}");
    Console.WriteLine($"Strategy:  {selectedStrategyConfiguration.Id}");
    Console.WriteLine($"Period:    {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");

    var engineResult = await engine.RunAsync(request);

    if (engineResult.Status == BacktestRunStatus.Failed)
    {
        Console.WriteLine($"Backtest failed: {engineResult.ErrorMessage}");
        return;
    }

    var summaryResult = await reportService.GenerateAsync(engineResult.RunId);
    reporter.PrintBacktestSummary(summaryResult);
    return;
}

var runVariantsArg = Array.Find(
    args,
    a => a.Equals("--run-variants", StringComparison.OrdinalIgnoreCase));

if (runVariantsArg is not null)
{
    var startDateArg2 = Array.Find(
        args,
        a => a.StartsWith("--backtest-start=", StringComparison.OrdinalIgnoreCase));

    var endDateArg2 = Array.Find(
        args,
        a => a.StartsWith("--backtest-end=", StringComparison.OrdinalIgnoreCase));

    var endDate2 = endDateArg2 is not null &&
                   DateTime.TryParse(endDateArg2["--backtest-end=".Length..], out var parsedEnd2)
        ? parsedEnd2
        : DateTime.Today.AddDays(-1);

    var startDate2 = startDateArg2 is not null &&
                     DateTime.TryParse(startDateArg2["--backtest-start=".Length..], out var parsedStart2)
        ? parsedStart2
        : endDate2.AddYears(-1);

    var allVariants = StrategyConfigurationLoader.LoadAll(configuration);

    IReadOnlyList<string> symbols2 = ["AAPL", "MSFT", "GOOG", "AMZN", "NVDA"];

    var longestLookback = 0;
    foreach (var v in allVariants)
    {
        if (v.LookbackDays > longestLookback)
            longestLookback = v.LookbackDays;
    }

    var variantRunRequest = new StrategyVariantRunRequest
    {
        NamePrefix = $"Backtest {startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}",
        StartDate = startDate2,
        EndDate = endDate2,
        HistoricalStartDate = startDate2.AddDays(-longestLookback),
        InitialCapital = 100_000m,
        Symbols = symbols2
    };

    var variantRunner = scope.ServiceProvider.GetRequiredService<IStrategyVariantRunner>();
    var reportService2 = scope.ServiceProvider.GetRequiredService<IBacktestSummaryReportService>();
    var reporter2 = scope.ServiceProvider.GetRequiredService<ConsoleReporter>();

    Console.WriteLine($"Running {allVariants.Count} strategy variant(s)...");
    Console.WriteLine($"Period: {startDate2:yyyy-MM-dd} to {endDate2:yyyy-MM-dd}");

    var batchResult = await variantRunner.RunAsync(allVariants, variantRunRequest);

    foreach (var variantResult in batchResult.Results)
    {
        Console.WriteLine();
        Console.WriteLine($"── Variant: {variantResult.StrategyId} ──");

        if (variantResult.EngineResult.Status == BacktestRunStatus.Failed)
        {
            Console.WriteLine($"  Failed: {variantResult.EngineResult.ErrorMessage}");
            continue;
        }

        var summary2 = await reportService2.GenerateAsync(variantResult.EngineResult.RunId);
        reporter2.PrintBacktestSummary(summary2);
    }

    return;
}

var runner = scope.ServiceProvider.GetRequiredService<SimulationRunner>();

await runner.RunAsync();