using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

var configuration =
    new ConfigurationBuilder()
        .SetBasePath(
            AppContext.BaseDirectory)
        .AddJsonFile(
            "appsettings.json",
            optional: false)
        .Build();

var connectionString =
    configuration.GetConnectionString(
        "TradingDb");

var options =
    new DbContextOptionsBuilder<TradingSimulatorDbContext>()
        .UseSqlServer(connectionString)
        .Options;

await using var db =
    new TradingSimulatorDbContext(options);

var portfolioRepository =
    new PortfolioRepository(db);

var observationResultRepository =
    new ObservationResultRepository(db);

var portfolioSnapshotRepository =
    new PortfolioSnapshotRepository(db);

var provider =
    new YahooMarketDataProvider();

var analyzer =
    new MarketObservationAnalyzer();

var recommendationService =
    new PortfolioRecommendationService();

var executionService =
    new PortfolioExecutionService();

var tradingEngine =
    new TradingEngine();

var rebalancingService =
    new RebalancingService();

var portfolioPerformanceService =
    new PortfolioPerformanceService();

var portfolioAllocationService =
    new PortfolioAllocationService();

var allocationEngine =
    new PortfolioAllocationEngine(
        portfolioAllocationService);

var positionSnapshotRepository =
    new PositionSnapshotRepository(db);

var portfolioContributionService =
    new PortfolioContributionService();

var portfolioRebalancingService =
    new PortfolioRebalancingService();

var symbols = new[]
{
    "MSFT",
    "AAPL",
    "NVDA",
    "META",
    "AMZN",
    "GOOG",
    "TSLA",
    "AMD",
    "NFLX",
    "INTC",
    "CRM",
    "ADBE",
    "UBER",
    "PLTR",
    "SNOW",
    "MU",
    "QCOM",
    "AVGO",
    "PANW",
    "NOW"
};

var from =
    DateTime.Today.AddDays(-14);

var results =
    new List<ObservationResult>();

var prices =
    new Dictionary<string, decimal>();

foreach (var symbol in symbols)
{
    try
    {
        var candles =
            await provider.GetHistoricalDailyAsync(
                symbol,
                from);

        var usableCandles =
            candles
                .OrderBy(x => x.Date)
                .ToList();

        if (usableCandles.Count < 5)
        {
            Console.WriteLine(
                $"Pomijam {symbol} - za mało świeczek: {usableCandles.Count}");

            continue;
        }

        var result =
            analyzer.Analyze(
                symbol,
                usableCandles);

        results.Add(result);

        prices[symbol] =
            usableCandles.Last().Close;
    }
    catch (Exception ex)
    {
        Console.WriteLine(
            $"Błąd pobierania danych dla {symbol}: {ex.Message}");
    }
}

var rankedResults =
    results
        .OrderByDescending(x => x.Score)
        .ToList();

var recommendations =
    recommendationService.Build(
        rankedResults);


await observationResultRepository.SaveAsync(
    rankedResults);

var rankingComparisonService =
    new RankingComparisonService();

var latestRun =
    await observationResultRepository
        .GetLatestRunAsync();

var previousRun =
    await observationResultRepository
        .GetPreviousRunAsync();

Console.WriteLine();
Console.WriteLine("===== MARKET RANKING =====");
Console.WriteLine();

foreach (var result in rankedResults)
{
    Console.WriteLine(
        $"{result.Symbol}" +
        $" | Momentum={result.MomentumPercent:N2}%" +
        $" | VolumeTrend={result.VolumeTrendPercent:N2}%" +
        $" | Volatility={result.VolatilityPercent:N2}%" +
        $" | AboveSMA5={result.IsAboveSma5}" +
        $" | Score={result.Score:N2}");
}

Console.WriteLine();
Console.WriteLine("===== PORTFOLIO RECOMMENDATION =====");
Console.WriteLine();

foreach (var recommendation in recommendations)
{
    Console.WriteLine(
        $"{recommendation.Symbol}" +
        $" | Allocation={recommendation.AllocationPercent:N2}%" +
        $" | Score={recommendation.Score:N2}");
}

var portfolio =
    await portfolioRepository
        .GetLatestAsync();

if (portfolio is null)
{
    Console.WriteLine();
    Console.WriteLine(
        "===== CREATE INITIAL PORTFOLIO =====");

    portfolio = new Portfolio
    {
        Name = "Main Portfolio",
        StartingCash = 10000m,
        Cash = 10000m
    };

    executionService.ExecuteRecommendations(
        portfolio,
        recommendations,
        prices,
        tradingEngine);

    await portfolioRepository.SaveAsync(
        portfolio);
}
else
{
    Console.WriteLine();
    Console.WriteLine(
        $"Using existing PortfolioId={portfolio.Id}");

    Console.WriteLine(
        $"Cash={portfolio.Cash:N2}");

    foreach (var position in portfolio.Positions)
    {
        Console.WriteLine(
            $"{position.Symbol} | Qty={position.Quantity:N4}");
    }
}

foreach (var position in portfolio.Positions)
{
    if (prices.TryGetValue(
        position.Symbol,
        out var currentPrice))
    {
        position.CurrentPrice =
            currentPrice;
    }
}

//await portfolioRepository.SaveAsync(
//    portfolio);

//var performance =
//    portfolioPerformanceService.Calculate(
//        portfolio);

//await portfolioSnapshotRepository.SaveAsync(
//    new PortfolioSnapshot
//    {
//        PortfolioId = portfolio.Id,

//        Timestamp = DateTime.UtcNow,

//        PortfolioValue =
//            Math.Round(
//                performance.PortfolioValue,
//                2),

//        Cash =
//            Math.Round(
//                portfolio.Cash,
//                2),

//        ProfitLoss =
//            Math.Round(
//                performance.ProfitLoss,
//                2),

//        ReturnPercent =
//            Math.Round(
//                performance.ReturnPercent,
//                2)
//    });

//Console.WriteLine();
//Console.WriteLine("===== GENERATED PORTFOLIO =====");
//Console.WriteLine();

//Console.WriteLine($"Cash = {portfolio.Cash:N2}");

//foreach (var position in portfolio.Positions)
//{
//    Console.WriteLine(
//        $"{position.Symbol}" +
//        $" | Qty={position.Quantity:N4}" +
//        $" | Avg={position.AveragePrice:N2}");
//}

//Console.WriteLine();
//Console.WriteLine("===== LAST RUN =====");

//if (latestRun is not null)
//{
//    Console.WriteLine(
//        $"RunId: {latestRun.Id}");

//    Console.WriteLine(
//        $"CreatedAt: {latestRun.CreatedAt}");
//}

//Console.WriteLine();
//Console.WriteLine("===== PREVIOUS RUN =====");

//if (latestRun is not null &&
//    previousRun is not null)
//{
//    var changes =
//        rankingComparisonService.Compare(
//            latestRun,
//            previousRun);

//    Console.WriteLine();
//    Console.WriteLine(
//        "===== RANK CHANGES =====");

//    Console.WriteLine();

//    foreach (var change in changes)
//    {
//        Console.WriteLine(
//            $"{change.Symbol}" +
//            $" | Previous={change.PreviousRank}" +
//            $" | Current={change.CurrentRank}" +
//            $" | Change={change.Change}");
//    }
//}

//if (latestRun is not null &&
//    previousRun is not null)
//{
//    var previousTop =
//        previousRun.Results
//            .OrderBy(x => x.RankPosition)
//            .Take(5)
//            .Select(x => x.Symbol)
//            .ToList();

//    Console.WriteLine();
//    Console.WriteLine(
//        "===== PREVIOUS PORTFOLIO =====");

//    foreach (var symbol in previousTop)
//    {
//        Console.WriteLine(symbol);
//    }
//}

////var currentSymbols =
////    previousRun.Results
////        .OrderBy(x => x.RankPosition)
////        .Take(5)
////        .Select(x => x.Symbol)
////        .ToHashSet();

////var newSymbols =
////    recommendations
////        .Select(x => x.Symbol)
////        .ToHashSet();

////var toSell =
////    currentSymbols
////        .Except(newSymbols)
////        .ToList();

////var toBuy =
////    newSymbols
////        .Except(currentSymbols)
////        .ToList();

//var symbolsToSell =
//    rebalancingService.GetSymbolsToSell(
//        portfolio,
//        recommendations);

//var symbolsToBuy =
//    rebalancingService.GetSymbolsToBuy(
//        portfolio,
//        recommendations);

//Console.WriteLine();
//Console.WriteLine(
//    "===== REBALANCING =====");

//Console.WriteLine();
//Console.WriteLine("SELL:");

//foreach (var symbol in symbolsToSell)
//{
//    var position =
//        portfolio.Positions
//            .FirstOrDefault(
//                x => x.Symbol == symbol);

//    if (position is null)
//    {
//        continue;
//    }

//    tradingEngine.Execute(
//        portfolio,
//        new TradingDecision
//        {
//            Symbol = symbol,
//            Action = TradeActionType.Sell,
//            Amount =
//                position.Quantity *
//                position.CurrentPrice
//        },
//        position.CurrentPrice);
//}

//Console.WriteLine();
//Console.WriteLine("BUY:");

//foreach (var symbol in symbolsToBuy)
//{
//    var recommendation =
//        recommendations
//            .First(x => x.Symbol == symbol);

//    var amount =
//        portfolio.Cash *
//        recommendation.AllocationPercent
//        / 100m;

//    tradingEngine.Execute(
//        portfolio,
//        new TradingDecision
//        {
//            Symbol = symbol,
//            Action = TradeActionType.Buy,
//            Amount = amount
//        },
//        prices[symbol]);
//}

await portfolioRepository.SaveAsync(
    portfolio);





//var totalValue =
//    portfolio.Cash +
//    portfolio.Positions.Sum(
//        x => x.Quantity *
//             x.CurrentPrice);

//var positionSnapshots =
//    portfolio.Positions
//        .Select(x =>
//            new PositionSnapshot
//            {
//                PortfolioId = portfolio.Id,

//                Timestamp =
//                    DateTime.UtcNow,

//                Symbol =
//                    x.Symbol,

//                Quantity =
//                    x.Quantity,

//                CurrentPrice =
//                    x.CurrentPrice,

//                PositionValue =
//                    x.Quantity *
//                    x.CurrentPrice,

//                Weight =
//                    x.Quantity *
//                    x.CurrentPrice /
//                    totalValue * 100m
//            })
//        .ToList();

//await positionSnapshotRepository
//    .SaveAsync(
//        positionSnapshots);



//var allocationInstructions =
//    allocationEngine.Calculate(
//        portfolio,
//        recommendations);

//foreach (var instruction in allocationInstructions)
//{
//    Console.WriteLine(
//        $"{(instruction.IsBuy ? "BUY" : "SELL")}" +
//        $" {instruction.Symbol}" +
//        $" {instruction.Difference:N2}");
//}

//var sellInstructions =
//    allocationInstructions
//        .Where(x => !x.IsBuy)
//        .ToList();

//foreach (var instruction in sellInstructions)
//{
//    var position =
//        portfolio.Positions
//            .FirstOrDefault(
//                x => x.Symbol ==
//                     instruction.Symbol);

//    if (position is null)
//    {
//        continue;
//    }

//    tradingEngine.Execute(
//        portfolio,
//        new TradingDecision
//        {
//            Symbol = instruction.Symbol,
//            Action = TradeActionType.Sell,
//            Amount = Math.Abs(
//                instruction.Difference)
//        },
//        position.CurrentPrice);
//}

//var trades =
//    allocationEngine.BuildTrades(
//        portfolio,
//        allocationInstructions);

//foreach (var trade in trades)
//{
//    tradingEngine.Execute(
//        portfolio,
//        new TradingDecision
//        {
//            Symbol = trade.Symbol,
//            Action = TradeActionType.Buy,
//            Amount = trade.Amount
//        },
//        prices[trade.Symbol]);
//}

portfolioRebalancingService.Execute(
    portfolio,
    recommendations,
    prices,
    allocationEngine,
    tradingEngine);

await portfolioRepository.SaveAsync(
portfolio);

var performance =
    portfolioPerformanceService.Calculate(
        portfolio);

Console.WriteLine();
Console.WriteLine(
    "===== PORTFOLIO PERFORMANCE =====");

Console.WriteLine(
    $"Portfolio Value: {performance.PortfolioValue:N2}");

Console.WriteLine(
    $"Profit/Loss: {performance.ProfitLoss:N2}");

Console.WriteLine(
    $"Return %: {performance.ReturnPercent:N2}%");

await portfolioSnapshotRepository.SaveAsync(
    new PortfolioSnapshot
    {
        PortfolioId = portfolio.Id,

        Timestamp = DateTime.UtcNow,

        PortfolioValue =
            Math.Round(
                performance.PortfolioValue,
                2),

        Cash =
            Math.Round(
                portfolio.Cash,
                2),

        ProfitLoss =
            Math.Round(
                performance.ProfitLoss,
                2),

        ReturnPercent =
            Math.Round(
                performance.ReturnPercent,
                2)
    });

var totalValue =
    portfolio.Cash +
    portfolio.Positions.Sum(
        x => x.Quantity *
             x.CurrentPrice);

var positionSnapshots =
    portfolio.Positions
        .Select(x =>
            new PositionSnapshot
            {
                PortfolioId = portfolio.Id,

                Timestamp =
                    DateTime.UtcNow,

                Symbol =
                    x.Symbol,

                Quantity =
                    x.Quantity,

                CurrentPrice =
                    x.CurrentPrice,

                PositionValue =
                    x.Quantity *
                    x.CurrentPrice,

                Weight =
                    x.Quantity *
                    x.CurrentPrice /
                    totalValue * 100m
            })
        .ToList();

await positionSnapshotRepository
    .SaveAsync(
        positionSnapshots);

Console.WriteLine();
Console.WriteLine(
    "===== PORTFOLIO ALLOCATION =====");

foreach (var position in portfolio.Positions)
{
    var value =
        position.Quantity *
        position.CurrentPrice;

    var weight =
        value / totalValue * 100m;

    Console.WriteLine(
        $"{position.Symbol} | " +
        $"Value={value:N2} | " +
        $"Weight={weight:N2}%");
}

Console.WriteLine(
    $"Cash | Weight={(portfolio.Cash / totalValue * 100m):N2}%");

Console.WriteLine();
Console.WriteLine(
    "===== TARGET ALLOCATION =====");
foreach (var recommendation in recommendations)
{
    var targetValue =
        portfolioAllocationService
            .CalculateTargetValue(
                portfolio,
                recommendation);

    var position =
        portfolio.Positions
            .FirstOrDefault(
                x => x.Symbol ==
                     recommendation.Symbol);

    var currentValue = 0m;

    if (position is not null)
    {
        currentValue =
            position.Quantity *
            position.CurrentPrice;
    }

    var difference =
        targetValue -
        currentValue;

    Console.WriteLine(
        $"{recommendation.Symbol}" +
        $" | Current={currentValue:N2}" +
        $" | Target={targetValue:N2}" +
        $" | Diff={difference:N2}");
}

Console.WriteLine();
Console.WriteLine(
    "===== REBALANCE BY WEIGHT =====");

foreach (var recommendation in recommendations)
{
    var difference =
        portfolioAllocationService
            .CalculateDifference(
                portfolio,
                recommendation);

    if (difference > -100m)
    {
        continue;
    }

    Console.WriteLine(
        $"SELL {recommendation.Symbol} " +
        $"{Math.Abs(difference):N2}");
}

foreach (var recommendation in recommendations)
{
    var difference =
        portfolioAllocationService
            .CalculateDifference(
                portfolio,
                recommendation);

    if (difference < 100m)
    {
        continue;
    }

    Console.WriteLine(
        $"BUY {recommendation.Symbol} " +
        $"{difference:N2}");
}

//Console.WriteLine();
//Console.WriteLine(
//    "===== ALLOCATION TRADES =====");

//foreach (var trade in trades)
//{
//    Console.WriteLine(
//        $"{trade.Symbol} | " +
//        $"{trade.Amount:N2}");
//}

Console.WriteLine();
Console.WriteLine(
    "===== POSITION CONTRIBUTION =====");

var contributions =
    portfolioContributionService
        .Calculate(
            portfolio);

foreach (var contribution in contributions)
{
    Console.WriteLine(
        $"{contribution.Symbol}" +
        $" | P/L={contribution.ProfitLoss:N2}" +
        $" | Return={contribution.ReturnPercent:N2}%");
}

if (previousRun is not null)
{
    Console.WriteLine(
        $"RunId: {previousRun.Id}");

    Console.WriteLine(
        $"CreatedAt: {previousRun.CreatedAt}");
}