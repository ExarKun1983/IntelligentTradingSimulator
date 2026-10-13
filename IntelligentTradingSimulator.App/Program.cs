using System;
using System.Collections.Generic;
using IntelligentTradingSimulator.App;
using IntelligentTradingSimulator.App.Configuration;
using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure;
using IntelligentTradingSimulator.Infrastructure.Reporting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

if (!BacktestSummaryCsvCommandLineParser.TryParse(args, out var csvFilePath, out var csvError))
{
    Console.WriteLine(csvError);
    return;
}

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

services.AddInfrastructure(connectionString, configuration);
services.AddSingleton(selectedStrategyConfiguration);
services.AddSingleton<BacktestEngineRequestFactory>();

services.AddSingleton<IScoringEngine>(
    _ => new CompositeScoringEngine(selectedStrategyConfiguration));
services.AddSingleton(
    sp => new MarketObservationAnalyzer(
        sp.GetRequiredService<IScoringEngine>()));
services.AddSingleton<MarketObservationAnalyzerFactory>();
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
services.AddSingleton<BacktestSummaryCsvExporter>();
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
services.AddSingleton<IStrategyComparisonReportService, StrategyComparisonReportService>();

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
    if (csvFilePath is not null && result.IsSuccess)
    {
        await scope.ServiceProvider.GetRequiredService<BacktestSummaryCsvExporter>()
            .ExportAsync(csvFilePath, [result]);
        Console.WriteLine($"Backtest summary exported to {csvFilePath}");
    }
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

    IReadOnlyList<string> symbols = StockUniverse.Symbols;

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
    if (csvFilePath is not null && summaryResult.IsSuccess)
    {
        await scope.ServiceProvider.GetRequiredService<BacktestSummaryCsvExporter>()
            .ExportAsync(csvFilePath, [summaryResult]);
        Console.WriteLine($"Backtest summary exported to {csvFilePath}");
    }
    return;
}

var runVariantsArg = Array.Find(
    args,
    a => a.Equals("--run-variants", StringComparison.OrdinalIgnoreCase));

if (runVariantsArg is not null)
{
    if (!StrategyComparisonCommandLineParser.TryParse(
        args, out var comparisonSortBy, out var comparisonBenchmark, out var comparisonError))
    {
        Console.WriteLine(comparisonError);
        return;
    }

    var variantsStartDateArg = Array.Find(
        args,
        a => a.StartsWith("--backtest-start=", StringComparison.OrdinalIgnoreCase));

    var variantsEndDateArg = Array.Find(
        args,
        a => a.StartsWith("--backtest-end=", StringComparison.OrdinalIgnoreCase));

    var variantsEndDate = variantsEndDateArg is not null &&
                   DateTime.TryParse(variantsEndDateArg["--backtest-end=".Length..], out var parsedVariantsEnd)
        ? parsedVariantsEnd
        : DateTime.Today.AddDays(-1);

    var variantsStartDate = variantsStartDateArg is not null &&
                     DateTime.TryParse(variantsStartDateArg["--backtest-start=".Length..], out var parsedVariantsStart)
        ? parsedVariantsStart
        : variantsEndDate.AddYears(-1);

    if (variantsStartDate > variantsEndDate)
    {
        Console.WriteLine(
            $"Error: --backtest-start ({variantsStartDate:yyyy-MM-dd}) must not be after --backtest-end ({variantsEndDate:yyyy-MM-dd}).");
        return;
    }

    var allVariants = StrategyConfigurationLoader.LoadAll(configuration);

    IReadOnlyList<string> variantsSymbols = StockUniverse.Symbols;

    var longestLookback = 0;
    foreach (var v in allVariants)
    {
        if (v.LookbackDays > longestLookback)
            longestLookback = v.LookbackDays;
    }

    var variantRunRequest = new StrategyVariantRunRequest
    {
        NamePrefix = $"Backtest {variantsStartDate:yyyy-MM-dd} to {variantsEndDate:yyyy-MM-dd}",
        StartDate = variantsStartDate,
        EndDate = variantsEndDate,
        HistoricalStartDate = variantsStartDate.AddDays(-longestLookback),
        InitialCapital = 100_000m,
        Symbols = variantsSymbols
    };

    var variantRunner = scope.ServiceProvider.GetRequiredService<IStrategyVariantRunner>();
    var variantsReportService = scope.ServiceProvider.GetRequiredService<IBacktestSummaryReportService>();
    var variantsReporter = scope.ServiceProvider.GetRequiredService<ConsoleReporter>();
    var comparisonService = scope.ServiceProvider.GetRequiredService<IStrategyComparisonReportService>();

    Console.WriteLine($"Running {allVariants.Count} strategy variant(s)...");
    Console.WriteLine($"Period: {variantsStartDate:yyyy-MM-dd} to {variantsEndDate:yyyy-MM-dd}");

    var batchResult = await variantRunner.RunAsync(allVariants, variantRunRequest);

    var summaries = new List<BacktestSummaryReportResult>(batchResult.Results.Count);

    foreach (var variantResult in batchResult.Results)
    {
        Console.WriteLine();
        Console.WriteLine($"── Variant: {variantResult.StrategyId} ──");

        if (variantResult.EngineResult.Status == BacktestRunStatus.Failed)
        {
            Console.WriteLine($"  Failed: {variantResult.EngineResult.ErrorMessage}");
            summaries.Add(new BacktestSummaryReportResult
            {
                IsSuccess = false,
                StrategyId = variantResult.StrategyId,
                ErrorMessage = variantResult.EngineResult.ErrorMessage
            });
            continue;
        }

        var variantSummary = await variantsReportService.GenerateAsync(variantResult.EngineResult.RunId);
        variantsReporter.PrintBacktestSummary(variantSummary);
        summaries.Add(variantSummary);
    }

    var comparisonReport = comparisonService.Build(summaries, comparisonSortBy, comparisonBenchmark);
    variantsReporter.PrintVariantComparisonReport(comparisonReport);

    return;
}

var runner = scope.ServiceProvider.GetRequiredService<SimulationRunner>();

await runner.RunAsync();