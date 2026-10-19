using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

using var consoleProgress = new ConsoleWorkflowProgress();
IWorkflowProgress progress = Array.Exists(args,
    a => a.Equals("--no-progress", StringComparison.OrdinalIgnoreCase))
    ? NullWorkflowProgress.Instance
    : consoleProgress;
using var commandOperation = progress.BeginOperation("Initializing application");

void FinishCommand(string message)
{
    commandOperation.Dispose();
    progress.Report(message, force: true);
    consoleProgress.Dispose();
}

try
{
if (!MarketDataSyncCommandLineParser.TryParse(
    args, out var synchronizeMarketData, out var syncFrom, out var syncTo, out var syncSymbols, out var syncError))
{
    Console.WriteLine(syncError);
    Environment.ExitCode = 1;
    return;
}

if (!StrategyTuningCommandLineParser.TryParse(args, out var tuningInput, out var tuningOutput, out var tuningError))
{
    Console.WriteLine(tuningError);
    Environment.ExitCode = 1;
    return;
}

if (tuningInput is not null)
{
    try
    {
        using var operation = progress.BeginOperation("Loading and generating tuning report");
        await new StrategyTuningReportWorkflow(
                new StrategyTuningAnalysisService(new StrategyComparisonReportService()))
            .RunAsync(tuningInput, tuningOutput);
    }
    catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or
                                    System.Text.Json.JsonException or ArgumentException)
    {
        Console.WriteLine($"Tuning analysis failed: {exception.Message}");
        Environment.ExitCode = 1;
    }
    FinishCommand(Environment.ExitCode == 0 ? "Workflow completed" : "Workflow failed");
    return;
}

if (!BacktestSummaryCsvCommandLineParser.TryParse(args, out var csvFilePath, out var csvError))
{
    Console.WriteLine(csvError);
    return;
}

if (!BacktestSnapshotCsvCommandLineParser.TryParse(args, out var snapshotCsvFilePath, out var snapshotCsvError))
{
    Console.WriteLine(snapshotCsvError);
    return;
}

if (!StrategyComparisonCsvCommandLineParser.TryParse(args, out var comparisonCsvFilePath, out var comparisonCsvError))
{
    Console.WriteLine(comparisonCsvError);
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

services.AddSingleton<IWorkflowProgress>(progress);
services.AddInfrastructure(connectionString, configuration);
services.AddSingleton(selectedStrategyConfiguration);
services.AddSingleton<BacktestEngineRequestFactory>();

services.AddSingleton<IScoringEngine>(
    _ => ScoringEngineFactory.Create(selectedStrategyConfiguration));
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
services.AddSingleton<IResultExplanationService, ResultExplanationService>();
services.AddSingleton<BacktestSummaryCsvExporter>();
services.AddSingleton<BacktestSnapshotCsvExporter>();
services.AddSingleton<StrategyComparisonCsvExporter>();
services.AddScoped<StrategyComparisonReportWorkflow>();
services.AddScoped<MarketDataWorkflow>();
services.AddScoped<PortfolioWorkflow>();
services.AddScoped<SimulationRunner>();
services.AddScoped<IBacktestSummaryReportService, BacktestSummaryReportService>();
services.AddScoped<BacktestReportWorkflow>();
services.AddScoped<BacktestHistoricalMarketDataService>();
services.AddScoped<IBenchmarkDataService, BenchmarkDataService>();
services.AddScoped<IBacktestBenchmarkService, BacktestBenchmarkService>();
services.AddScoped<IBacktestDayProcessor, BacktestDayProcessor>();
services.AddScoped<IBacktestEngine, BacktestEngine>();
services.AddScoped<IStrategyVariantRunner, StrategyVariantRunner>();
services.AddSingleton<IStrategyComparisonReportService, StrategyComparisonReportService>();

await using var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();

if (synchronizeMarketData)
{
    var historicalData = scope.ServiceProvider.GetRequiredService<BacktestHistoricalMarketDataService>();
    foreach (var symbol in syncSymbols)
    {
        using var operation = progress.BeginOperation("Synchronizing historical market data", symbol);
        var candles = await historicalData.GetHistoricalDailyRangeAsync(symbol, syncFrom, syncTo);
        Console.WriteLine($"{symbol}: {candles.Count} candles available for {syncFrom:yyyy-MM-dd} to {syncTo:yyyy-MM-dd}.");
    }
    FinishCommand("Market-data synchronization completed");
    return;
}

var backtestRunIdArg = Array.Find(
    args,
    a => a.StartsWith("--backtest-run-id=", StringComparison.OrdinalIgnoreCase));

if (backtestRunIdArg is not null &&
    int.TryParse(
        backtestRunIdArg["--backtest-run-id=".Length..],
        out var backtestRunId))
{
    var reportCompleted = await scope.ServiceProvider.GetRequiredService<BacktestReportWorkflow>()
        .RunAsync(backtestRunId, csvFilePath, snapshotCsvFilePath);
    FinishCommand(reportCompleted ? "Workflow completed" : "Workflow failed");
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
        FinishCommand("Workflow failed");
        return;
    }

    var reportCompleted = await scope.ServiceProvider.GetRequiredService<BacktestReportWorkflow>()
        .RunAsync(engineResult.RunId, csvFilePath, snapshotCsvFilePath);
    FinishCommand(reportCompleted ? "Workflow completed" : "Workflow failed");
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

    var variantRunRequest = new StrategyVariantRunRequest
    {
        NamePrefix = $"Backtest {variantsStartDate:yyyy-MM-dd} to {variantsEndDate:yyyy-MM-dd}",
        StartDate = variantsStartDate,
        EndDate = variantsEndDate,
        InitialCapital = 100_000m,
        Symbols = variantsSymbols
    };

    var variantRunner = scope.ServiceProvider.GetRequiredService<IStrategyVariantRunner>();
    var variantsReportService = scope.ServiceProvider.GetRequiredService<IBacktestSummaryReportService>();
    var variantsReporter = scope.ServiceProvider.GetRequiredService<ConsoleReporter>();

    Console.WriteLine($"Running {allVariants.Count} strategy variant(s)...");
    Console.WriteLine($"Period: {variantsStartDate:yyyy-MM-dd} to {variantsEndDate:yyyy-MM-dd}");

    var batchResult = await variantRunner.RunAsync(allVariants, variantRunRequest);

    var summaries = new List<BacktestSummaryReportResult>(batchResult.Results.Count);
    var allSucceeded = true;

    foreach (var variantResult in batchResult.Results)
    {
        Console.WriteLine();
        Console.WriteLine($"── Variant: {variantResult.StrategyId} ──");

        if (variantResult.EngineResult.Status == BacktestRunStatus.Failed)
        {
            allSucceeded = false;
            Console.WriteLine($"  Failed: {variantResult.EngineResult.ErrorMessage}");
            summaries.Add(new BacktestSummaryReportResult
            {
                IsSuccess = false,
                StrategyId = variantResult.StrategyId,
                StartDate = variantsStartDate.Date,
                EndDate = variantsEndDate.Date,
                HistoricalStartDate = variantResult.HistoricalStartDate,
                ErrorMessage = variantResult.EngineResult.ErrorMessage
            });
            continue;
        }

        progress.Context = progress.Context with
        {
            Workflow = "backtest-report",
            StrategyId = variantResult.StrategyId,
            RunId = variantResult.EngineResult.RunId,
            Date = null,
            CompletedDates = null,
            TotalDates = null,
            CompletedSymbols = null,
            TotalSymbols = null,
            VariantIndex = summaries.Count + 1
        };
        BacktestSummaryReportResult variantSummary;
        using (progress.BeginOperation("Loading report database data and generating summary"))
            variantSummary = await variantsReportService.GenerateAsync(variantResult.EngineResult.RunId);
        variantSummary.StartDate = variantsStartDate.Date;
        variantSummary.EndDate = variantsEndDate.Date;
        variantSummary.HistoricalStartDate = variantResult.HistoricalStartDate;
        allSucceeded &= variantSummary.IsSuccess;
        variantsReporter.PrintBacktestSummary(variantSummary);
        summaries.Add(variantSummary);
    }

    progress.Context = new WorkflowProgressContext { Workflow = "strategy-comparison" };
    await scope.ServiceProvider.GetRequiredService<StrategyComparisonReportWorkflow>()
        .RunAsync(summaries, comparisonSortBy, comparisonBenchmark, comparisonCsvFilePath);

    FinishCommand(allSucceeded ? "Workflow completed" : "Workflow completed with variant failures");
    return;
}

var runner = scope.ServiceProvider.GetRequiredService<SimulationRunner>();

await runner.RunAsync();
FinishCommand("Workflow completed");
}
catch (OperationCanceledException)
{
    FinishCommand("Workflow cancelled");
    throw;
}
catch
{
    FinishCommand("Workflow failed");
    throw;
}