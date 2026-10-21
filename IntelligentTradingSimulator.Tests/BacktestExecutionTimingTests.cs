using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Enums;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestExecutionTimingTests
{
    private static readonly DateTime Friday = new(2024, 5, 24);

    [Fact]
    public async Task CloseSignal_DoesNotTradeUntilLaterInstrumentOpen_AndPersistsActualDates()
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] = History(Friday)
            .Append(Candle(Friday.AddDays(4), 137m, 150m)).ToList();
        var portfolio = CashPortfolio();
        var state = new BacktestExecutionState();

        var signal = await fixture.Day(Friday, portfolio, state, ["AAA"]);

        Assert.Empty(portfolio.Trades);
        Assert.Equal(0, signal.Snapshot.TradesExecuted);
        Assert.False(signal.Snapshot.RebalanceExecuted);
        Assert.Equal(Friday, state.Pending["AAA"].SignalDate);
        Assert.Equal(DateOnly.FromDateTime(Friday),
            Assert.Single(fixture.Features.Rows).Features.AnalysisDate);

        var execution = await fixture.Day(Friday.AddDays(4), portfolio, state, ["AAA"]);

        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(TradeActionType.Buy, trade.Action);
        Assert.Equal(Friday.AddDays(4), trade.Timestamp);
        Assert.Equal(137m, trade.Price);
        Assert.Equal(1, execution.Snapshot.TradesExecuted);
        Assert.True(execution.Snapshot.RebalanceExecuted);
        Assert.Equal(Friday.AddDays(4), execution.Snapshot.Date);
        Assert.Equal(Friday.AddDays(4), state.Pending["AAA"].SignalDate);
        Assert.Equal(new[] { DateOnly.FromDateTime(Friday), DateOnly.FromDateTime(Friday.AddDays(4)) },
            fixture.Features.Rows.Select(x => x.Features.AnalysisDate));
        Assert.Equal(TradingPrecisionPolicy.RoundReport(portfolio.TotalValue),
            execution.Snapshot.PortfolioValue);
        Assert.Equal(TradingPrecisionPolicy.RoundReport(portfolio.Cash), execution.Snapshot.Cash);
        AssertReconciles(portfolio, 1_000m);
    }

    [Theory]
    [InlineData("Close")]
    [InlineData("High")]
    [InlineData("Low")]
    [InlineData("Volume")]
    public async Task ExecutionCandleNonOpenFields_CannotChangeOpeningAllocationOrRiskTrades(string field)
    {
        async Task<Portfolio> Execute(bool perturb)
        {
            var fixture = new Fixture();
            var configuration = Configuration();
            configuration.StopLossPercent = 10m;
            configuration.TrailingStopPercent = 10m;
            var portfolio = CashPortfolio();
            portfolio.StartingCash = 2_000m;
            portfolio.Positions.Add(Holding("AAA", 10m, 100m));
            var state = new BacktestExecutionState();
            Seed(state, portfolio, Friday, [("AAA", 25m), ("BBB", 75m)], configuration);
            foreach (var symbol in new[] { "AAA", "BBB" })
            {
                var candle = Candle(Friday.AddDays(4), symbol == "AAA" ? 120m : 30m, 110m);
                if (perturb)
                {
                    switch (field)
                    {
                        case "Close": candle.Close = 1m; break;
                        case "High": candle.High = 10_000m; break;
                        case "Low": candle.Low = 0.01m; break;
                        case "Volume": candle.Volume = 1_000_000_000m; break;
                    }
                }
                fixture.Provider.Data[symbol] = History(Friday).Append(candle).ToList();
            }

            var result = await fixture.Day(Friday.AddDays(4), portfolio, state,
                ["AAA", "BBB"], configuration);
            Assert.Equal(2, result.Snapshot.TradesExecuted);
            Assert.Equal(TradeActionType.Sell, portfolio.Trades[0].Action);
            Assert.Equal(TradeActionType.Buy, portfolio.Trades[1].Action);
            AssertReconciles(portfolio, 1_000m, ("AAA", 10m));
            return portfolio;
        }

        var baseline = await Execute(false);
        var changed = await Execute(true);

        Assert.Equal(TradeRows(baseline), TradeRows(changed));
        Assert.Equal(baseline.Cash, changed.Cash);
        Assert.Equal(baseline.Positions.Select(x => (x.Symbol, x.Quantity)),
            changed.Positions.Select(x => (x.Symbol, x.Quantity)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningGap_DoesNotAdvanceCloseEvaluatedTrailingHigh_EvenAfterPartialSale(bool partialSale)
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] = [Candle(Friday.AddDays(4), 200m, 150m)];
        var portfolio = CashPortfolio();
        portfolio.Positions.Add(Holding("AAA", 10m, 100m));
        var configuration = Configuration();
        configuration.TrailingStopPercent = 10m;
        var state = new BacktestExecutionState();
        if (partialSale)
            Seed(state, portfolio, Friday, [("AAA", 25m)], configuration);

        await fixture.Day(Friday.AddDays(4), portfolio, state, [], configuration);

        if (partialSale)
        {
            var sale = Assert.Single(portfolio.Trades);
            Assert.Equal(TradeActionType.Sell, sale.Action);
            Assert.Equal(6.25m, sale.Quantity);
            Assert.Equal(3.75m, Assert.Single(portfolio.Positions).Quantity);
            AssertReconciles(portfolio, 1_000m, ("AAA", 10m));
        }
        else
            Assert.Empty(portfolio.Trades);
        Assert.Empty(state.Pending);
        Assert.Equal(150m, Assert.Single(portfolio.Positions).HighestPriceSinceEntry);
    }

    [Fact]
    public async Task NewEntryOpen_EstablishesTrailingHigh_ButCloseStopWaitsForNextOpen()
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] =
        [
            Candle(Friday.AddDays(4), 200m, 150m),
            Candle(Friday.AddDays(5), 140m, 130m)
        ];
        var portfolio = CashPortfolio();
        var configuration = Configuration();
        configuration.TrailingStopPercent = 10m;
        var state = new BacktestExecutionState();
        Seed(state, portfolio, Friday, [("AAA", 100m)], configuration);

        await fixture.Day(Friday.AddDays(4), portfolio, state, [], configuration);

        Assert.Equal(TradeActionType.Buy, Assert.Single(portfolio.Trades).Action);
        Assert.Equal(200m, Assert.Single(portfolio.Positions).HighestPriceSinceEntry);
        Assert.Equal(ExitReason.TrailingStop, state.Pending["AAA"].ExitReason);
        Assert.Equal(Friday.AddDays(4), state.Pending["AAA"].SignalDate);

        var exit = await fixture.Day(Friday.AddDays(5), portfolio, state, [], configuration);

        Assert.Equal(2, portfolio.Trades.Count);
        Assert.Equal(ExitReason.TrailingStop, portfolio.Trades[1].ExitReason);
        Assert.Equal(140m, portfolio.Trades[1].Price);
        Assert.Equal(Friday.AddDays(5), portfolio.Trades[1].Timestamp);
        Assert.Empty(portfolio.Positions);
        Assert.Equal(1, exit.Snapshot.TradesExecuted);
        Assert.False(exit.Snapshot.RebalanceExecuted);
        AssertReconciles(portfolio, 1_000m);
    }

    [Fact]
    public void ZeroQuantityHolding_DoesNotGenerateRiskExit()
    {
        var portfolio = CashPortfolio();
        portfolio.Positions.Add(Holding("AAA", 0m, 100m));
        var configuration = Configuration();
        configuration.StopLossPercent = 10m;
        configuration.TrailingStopPercent = 10m;
        var state = new BacktestExecutionState();

        state.GenerateAfterClose(portfolio, Friday,
            new Dictionary<string, decimal> { ["AAA"] = 50m }, [], [], configuration);

        Assert.Empty(state.Pending);
    }

    [Fact]
    public async Task WeekendHolidayAndPerSymbolGaps_WaitForEachSymbolsFirstActualOpen()
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] = History(Friday)
            .Append(Candle(Friday.AddDays(4), 120m, 121m)).ToList();
        fixture.Provider.Data["BBB"] = History(Friday)
            .Append(Candle(Friday.AddDays(5), 130m, 131m)).ToList();
        var state = new BacktestExecutionState();
        var portfolio = CashPortfolio();

        await fixture.Day(Friday, portfolio, state, ["AAA", "BBB"]);
        Assert.Equal(2, state.Pending.Count);
        for (var offset = 1; offset <= 3; offset++)
        {
            var missing = await fixture.Day(Friday.AddDays(offset), portfolio, state, ["AAA", "BBB"]);
            Assert.Empty(missing.Observations);
            Assert.Equal(0, missing.Snapshot.TradesExecuted);
            Assert.False(missing.Snapshot.RebalanceExecuted);
            Assert.All(state.Pending.Values, x => Assert.Equal(Friday, x.SignalDate));
        }
        Assert.Empty(portfolio.Trades);
        Assert.Equal(2, fixture.Features.Rows.Count);

        var tuesday = await fixture.Day(Friday.AddDays(4), portfolio, state, ["AAA", "BBB"]);
        Assert.Equal("AAA", Assert.Single(portfolio.Trades).Symbol);
        Assert.Single(tuesday.Observations);
        Assert.Equal(2, tuesday.Recommendations.Count);
        Assert.Equal(Friday, state.Pending["BBB"].SignalDate);

        var wednesday = await fixture.Day(Friday.AddDays(5), portfolio, state, ["AAA", "BBB"]);
        Assert.Equal(2, portfolio.Trades.Count);
        Assert.Equal("BBB", portfolio.Trades[1].Symbol);
        Assert.Equal(Friday.AddDays(5), portfolio.Trades[1].Timestamp);
        Assert.Equal(130m, portfolio.Trades[1].Price);
        Assert.Equal(Friday.AddDays(4), state.Pending["AAA"].SignalDate);
        Assert.Single(wednesday.Observations);
        Assert.Equal(4, fixture.Features.Rows.Count);
        AssertReconciles(portfolio, 1_000m);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("0.0000000000001")]
    [InlineData("10000000000000000")]
    public async Task InvalidOrMissingOpen_RetainsOriginalIntentWithoutCloseFallback_ThenAttemptsOnlyOnce(string invalid)
    {
        var fixture = new Fixture();
        const decimal validOpen = 100.12345678901249m;
        fixture.Provider.Data["AAA"] =
        [
            Candle(Friday.AddDays(1), decimal.Parse(invalid, System.Globalization.CultureInfo.InvariantCulture), 125m),
            Candle(Friday.AddDays(3), validOpen, 150m),
            Candle(Friday.AddDays(4), 99m, 151m)
        ];
        var portfolio = CashPortfolio();
        var state = new BacktestExecutionState();
        Seed(state, portfolio, Friday, [("AAA", 100m)]);
        var original = state.Pending["AAA"];

        foreach (var offset in new[] { 1, 2 })
        {
            var result = await fixture.Day(Friday.AddDays(offset), portfolio, state, []);
            Assert.Empty(portfolio.Trades);
            Assert.Equal(0, result.Snapshot.TradesExecuted);
            Assert.Same(original, state.Pending["AAA"]);
            Assert.Equal(Friday, state.Pending["AAA"].SignalDate);
        }

        await fixture.Day(Friday.AddDays(3), portfolio, state, []);
        Assert.Empty(state.Pending);
        var trade = Assert.Single(portfolio.Trades);
        Assert.Equal(100.123456789012m, trade.Price);
        Assert.Equal(Friday.AddDays(3), trade.Timestamp);
        await fixture.Day(Friday.AddDays(3), portfolio, state, []);
        await fixture.Day(Friday.AddDays(4), portfolio, state, []);
        Assert.Single(portfolio.Trades);
        AssertReconciles(portfolio, 1_000m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExitReason.StopLoss)]
    [InlineData(ExitReason.TrailingStop)]
    public async Task HeldSymbolAbsentFromUniverse_LiquidationRequiresFreshCloseAndLaterActualOpen(ExitReason? reason)
    {
        var fixture = new Fixture();
        var close = reason == ExitReason.StopLoss ? 80m : 100m;
        fixture.Provider.Data["AAA"] =
        [
            Candle(Friday.AddDays(1), 90m, close),
            Candle(Friday.AddDays(3), 60m, 65m)
        ];
        fixture.Provider.Data["BBB"] = History(Friday)
            .Concat(Enumerable.Range(1, 3).Select(i => Candle(Friday.AddDays(i), 110m + i, 111m + i)))
            .ToList();
        var portfolio = new Portfolio { StartingCash = 1_000m };
        var position = Holding("AAA", 10m, 100m);
        if (reason == ExitReason.TrailingStop)
            position.HighestPriceSinceEntry = 120m;
        portfolio.Positions.Add(position);
        var configuration = Configuration();
        configuration.StopLossPercent = reason == ExitReason.StopLoss ? 10m : null;
        configuration.TrailingStopPercent = reason == ExitReason.TrailingStop ? 10m : null;
        var state = new BacktestExecutionState();

        await fixture.Day(Friday, portfolio, state, ["BBB"], configuration);
        Assert.False(state.Pending.ContainsKey("AAA"));
        Assert.Contains(fixture.Provider.Fetches, x => x.Symbol == "AAA" && x.To == Friday);

        var signal = await fixture.Day(Friday.AddDays(1), portfolio, state, ["BBB"], configuration);
        Assert.DoesNotContain(portfolio.Trades, x => x.Symbol == "AAA");
        Assert.Equal(0, signal.Snapshot.TradesExecuted);
        Assert.Equal(Friday.AddDays(1), state.Pending["AAA"].SignalDate);
        Assert.Equal(reason, state.Pending["AAA"].ExitReason);
        Assert.Equal(0m, state.Pending["AAA"].AllocationPercent);

        await fixture.Day(Friday.AddDays(2), portfolio, state, ["BBB"], configuration);
        Assert.Equal(Friday.AddDays(1), state.Pending["AAA"].SignalDate);
        Assert.DoesNotContain(portfolio.Trades, x => x.Symbol == "AAA");

        var execution = await fixture.Day(Friday.AddDays(3), portfolio, state, ["BBB"], configuration);
        var sale = Assert.Single(portfolio.Trades, x => x.Symbol == "AAA");
        Assert.Equal(TradeActionType.Sell, sale.Action);
        Assert.Equal(reason ?? ExitReason.Rebalance, sale.ExitReason);
        Assert.Equal(60m, sale.Price);
        Assert.Equal(10m, sale.Quantity);
        Assert.Equal(Friday.AddDays(3), sale.Timestamp);
        Assert.DoesNotContain(portfolio.Positions, x => x.Symbol == "AAA");
        Assert.Equal(portfolio.Trades.Count(x => x.Timestamp == Friday.AddDays(3)),
            execution.Snapshot.TradesExecuted);
        AssertReconciles(portfolio, 0m, ("AAA", 10m));
    }

    [Fact]
    public async Task RiskOnlyOpeningExit_IsCountedAsTradeButNotRebalance()
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] = [Candle(Friday.AddDays(4), 70m, 90m)];
        var portfolio = new Portfolio { StartingCash = 1_000m };
        portfolio.Positions.Add(Holding("AAA", 10m, 100m));
        var state = new BacktestExecutionState();
        var configuration = Configuration();
        configuration.StopLossPercent = 10m;
        state.GenerateAfterClose(portfolio, Friday,
            new Dictionary<string, decimal> { ["AAA"] = 80m }, [], [], configuration);

        var result = await fixture.Day(Friday.AddDays(4), portfolio, state, [], configuration);

        Assert.Equal(1, result.Snapshot.TradesExecuted);
        Assert.False(result.Snapshot.RebalanceExecuted);
        Assert.Equal(ExitReason.StopLoss, Assert.Single(portfolio.Trades).ExitReason);
        Assert.Equal(700m, result.Snapshot.Cash);
        Assert.Equal(-300m, result.Snapshot.ProfitLoss);
        Assert.Equal(-30m, result.Snapshot.ReturnPercent);
    }

    [Fact]
    public void PendingTargetsAreSticky_OnlyNewRiskOverrides_AndRiskDateIsAlsoSticky()
    {
        var state = new BacktestExecutionState();
        var portfolio = CashPortfolio();
        portfolio.Positions.Add(Holding("AAA", 10m, 100m));
        var configuration = Configuration();
        configuration.StopLossPercent = 10m;
        Seed(state, portfolio, Friday, [("AAA", 20m)], configuration);
        var original = state.Pending["AAA"];

        Seed(state, portfolio, Friday.AddDays(1), [("AAA", 80m)], configuration);
        Assert.Same(original, state.Pending["AAA"]);
        Assert.Equal(20m, state.Pending["AAA"].AllocationPercent);
        state.GenerateAfterClose(portfolio, Friday.AddDays(2),
            new Dictionary<string, decimal> { ["AAA"] = 80m }, [], [], configuration);
        var risk = state.Pending["AAA"];
        Assert.Equal(Friday.AddDays(2), risk.SignalDate);
        Assert.Equal(ExitReason.StopLoss, risk.ExitReason);
        state.GenerateAfterClose(portfolio, Friday.AddDays(3),
            new Dictionary<string, decimal> { ["AAA"] = 70m }, [], [], configuration);
        Assert.Same(risk, state.Pending["AAA"]);

        Execute(state, portfolio, Friday.AddDays(2), ("AAA", 75m));
        Assert.Empty(portfolio.Trades);
        Execute(state, portfolio, Friday.AddDays(4), ("AAA", 60m));
        Assert.Equal(Friday.AddDays(4), Assert.Single(portfolio.Trades).Timestamp);
        Assert.Empty(state.Pending);
    }

    [Theory]
    [InlineData("50")]
    [InlineData("200")]
    public void BelowThresholdOrUnaffordableIntent_IsConsumedAtFirstEligibleOpen_AndFreshCloseMayReissue(string cashText)
    {
        var cash = decimal.Parse(cashText, System.Globalization.CultureInfo.InvariantCulture);
        var portfolio = CashPortfolio(cash);
        var state = new BacktestExecutionState();
        Seed(state, portfolio, Friday, [("AAA", 100m)]);

        Execute(state, portfolio, Friday.AddDays(1), ("AAA", 1_000_000_000_000m));
        Assert.Empty(portfolio.Trades);
        Assert.Empty(state.Pending);
        Execute(state, portfolio, Friday.AddDays(2), ("AAA", 1m));
        Assert.Empty(portfolio.Trades);

        Seed(state, portfolio, Friday.AddDays(2), [("AAA", 100m)]);
        Assert.Equal(Friday.AddDays(2), state.Pending["AAA"].SignalDate);
        Execute(state, portfolio, Friday.AddDays(3), ("AAA", 1m));
        Assert.Equal(cash >= 100m ? 1 : 0, portfolio.Trades.Count);
        Assert.Empty(state.Pending);
        Assert.True(portfolio.Cash >= 0m);
    }

    [Fact]
    public void GappedOpeningValuation_SellsBeforeCompetingBuys_WithDeterministicCashAndQuantityPrecision()
    {
        Portfolio Run(bool reverse)
        {
            var portfolio = new Portfolio { StartingCash = 1_000m, Cash = 0.00000001m };
            portfolio.Positions.Add(Holding("ZZZ", 10m, 100m));
            var state = new BacktestExecutionState();
            var targets = new[] { ("ZZZ", 0m), ("BBB", 100m), ("AAA", 100m) };
            Seed(state, portfolio, Friday, reverse ? targets.Reverse().ToArray() : targets);
            Execute(state, portfolio, Friday.AddDays(4),
                ("ZZZ", 73.333333333333m), ("BBB", 17.000000000001m), ("AAA", 31.000000000001m));

            Assert.Equal(2, portfolio.Trades.Count);
            var sale = portfolio.Trades[0];
            Assert.Equal(("ZZZ", TradeActionType.Sell, 10m, 73.333333333333m),
                (sale.Symbol, sale.Action, sale.Quantity, sale.Price));
            Assert.Equal(("AAA", TradeActionType.Buy),
                (portfolio.Trades[1].Symbol, portfolio.Trades[1].Action));
            Assert.DoesNotContain(portfolio.Positions, x => x.Symbol == "ZZZ");
            Assert.All(portfolio.Positions, x => Assert.True(x.Quantity > 0m));
            Assert.True(portfolio.Cash >= 0m);
            Assert.Empty(state.Pending);
            AssertReconciles(portfolio, 0.00000001m, ("ZZZ", 10m));
            return portfolio;
        }

        var first = Run(false);
        var second = Run(true);
        Assert.Equal(TradeRows(first), TradeRows(second));
        Assert.Equal(first.Cash, second.Cash);
    }

    [Fact]
    public async Task EngineEndDateSignalRemainsUnfilled_AndSameEngineRerunsAndVariantsHaveFreshState()
    {
        var fixture = new Fixture();
        fixture.Provider.Data["AAA"] = History(Friday)
            .Append(Candle(Friday.AddDays(4), 125m, 130m)).ToList();
        var runs = new RunRepository();
        var recording = new RecordingProcessor(fixture.Processor);
        var engine = new BacktestEngine(runs, recording);

        async Task<BacktestRun> Run(DateTime end, decimal cap)
        {
            var configuration = Configuration();
            configuration.MaxPositionWeight = cap;
            var result = await engine.RunAsync(new BacktestEngineRequest
            {
                Name = "causal-regression",
                StrategyId = configuration.Id,
                StrategyConfiguration = configuration,
                StartDate = Friday,
                EndDate = end,
                HistoricalStartDate = Friday.AddDays(-4),
                InitialCapital = 1_000m,
                Symbols = ["AAA"]
            });
            Assert.Equal(BacktestRunStatus.Completed, result.Status);
            var run = runs.Rows.Single(x => x.Id == result.RunId);
            Assert.NotNull(run.Portfolio);
            Assert.Equal(TradingPrecisionPolicy.RoundReport(run.Portfolio.TotalValue), result.FinalCapital);
            return run;
        }

        var endDateOnly = await Run(Friday, 100m);
        Assert.Empty(endDateOnly.Portfolio!.Trades);
        Assert.Single(recording.States[endDateOnly.Id].Pending);
        Assert.Equal(Friday, recording.States[endDateOnly.Id].Pending["AAA"].SignalDate);

        var first = await Run(Friday.AddDays(4), 100m);
        var repeated = await Run(Friday.AddDays(4), 100m);
        var capped = await Run(Friday.AddDays(4), 25m);

        Assert.Equal(4, recording.States.Values.Distinct().Count());
        Assert.Equal(TradeRows(first.Portfolio!), TradeRows(repeated.Portfolio!));
        Assert.Equal(first.FinalCapital, repeated.FinalCapital);
        Assert.Equal(first.Portfolio!.Cash, repeated.Portfolio!.Cash);
        var fullTrade = Assert.Single(first.Portfolio.Trades);
        var cappedTrade = Assert.Single(capped.Portfolio!.Trades);
        Assert.Equal(Friday.AddDays(4), fullTrade.Timestamp);
        Assert.Equal(125m, fullTrade.Price);
        Assert.Equal(fullTrade.Quantity / 4m, cappedTrade.Quantity);
        Assert.Equal(750m, capped.Portfolio.Cash);
        Assert.Equal(Friday.AddDays(4), recording.States[first.Id].Pending["AAA"].SignalDate);
        foreach (var run in runs.Rows)
        {
            var snapshots = fixture.Snapshots.Rows.Where(x => x.BacktestRunId == run.Id).ToList();
            Assert.Equal(run.Portfolio!.Trades.Count, snapshots.Sum(x => x.TradesExecuted));
            Assert.Equal(run.FinalCapital, snapshots[^1].PortfolioValue);
            AssertReconciles(run.Portfolio, 1_000m);
        }
    }

    private static StrategyConfiguration Configuration() => new()
    {
        Id = "timing-regression", UseRelativeStrength = false, MaxPositionWeight = 100m
    };

    private static Portfolio CashPortfolio(decimal cash = 1_000m) => new()
    {
        StartingCash = cash, Cash = cash
    };

    private static Position Holding(string symbol, decimal quantity, decimal price) => new()
    {
        Symbol = symbol, Quantity = quantity, AveragePrice = price, CurrentPrice = price
    };

    private static MarketCandle Candle(DateTime date, decimal open, decimal close) => new()
    {
        Date = date, Open = open, Close = close,
        High = Math.Max(open, close) + 1m, Low = Math.Min(open, close) - 1m, Volume = 1_000_000m
    };

    private static List<MarketCandle> History(DateTime last) =>
        Enumerable.Range(0, 5).Select(i => Candle(last.AddDays(i - 4), 100m + i, 101m + i)).ToList();

    private static void Seed(
        BacktestExecutionState state, Portfolio portfolio, DateTime date,
        IReadOnlyList<(string Symbol, decimal Weight)> targets, StrategyConfiguration? configuration = null)
    {
        var closes = targets.Select(x => x.Symbol).Concat(portfolio.Positions.Select(x => x.Symbol))
            .Distinct().ToDictionary(x => x, _ => 100m);
        state.GenerateAfterClose(portfolio, date, closes,
            targets.Select(x => new StockRecommendation { Symbol = x.Symbol, AllocationPercent = x.Weight }).ToList(),
            targets.Select(x => new ObservationResult { Symbol = x.Symbol }).ToList(),
            configuration ?? Configuration());
    }

    private static void Execute(
        BacktestExecutionState state, Portfolio portfolio, DateTime date,
        params (string Symbol, decimal Open)[] opens) =>
        state.ExecuteOpen(portfolio, date, opens.ToDictionary(x => x.Symbol, x => x.Open),
            new PortfolioAllocationEngine(new PortfolioAllocationService()), new TradingEngine());

    private static (DateTime, string, TradeActionType, decimal, decimal, ExitReason?)[] TradeRows(Portfolio portfolio) =>
        portfolio.Trades.Select(x => (x.Timestamp, x.Symbol, x.Action, x.Quantity, x.Price, x.ExitReason)).ToArray();

    private static void AssertReconciles(
        Portfolio portfolio, decimal initialCash, params (string Symbol, decimal Quantity)[] initialPositions)
    {
        var cash = initialCash;
        var quantities = initialPositions.ToDictionary(x => x.Symbol, x => x.Quantity);
        foreach (var trade in portfolio.Trades)
        {
            Assert.True(trade.Quantity > 0m);
            var notional = Math.Round(trade.Quantity * trade.Price,
                TradingPrecisionPolicy.CashScale, MidpointRounding.AwayFromZero);
            var previous = quantities.GetValueOrDefault(trade.Symbol);
            if (trade.Action == TradeActionType.Buy)
            {
                cash -= notional;
                quantities[trade.Symbol] = previous + trade.Quantity;
            }
            else
            {
                Assert.True(trade.Quantity <= previous);
                cash += notional;
                quantities[trade.Symbol] = previous - trade.Quantity;
            }
            Assert.True(cash >= 0m);
        }
        Assert.Equal(cash, portfolio.Cash);
        foreach (var (symbol, quantity) in quantities)
            Assert.Equal(quantity, portfolio.Positions.SingleOrDefault(x => x.Symbol == symbol)?.Quantity ?? 0m);
        Assert.Equal(portfolio.Cash + portfolio.Positions.Sum(x => x.Quantity * x.CurrentPrice), portfolio.TotalValue);
    }

    private sealed class Fixture
    {
        public Provider Provider { get; } = new();
        public SnapshotRepository Snapshots { get; } = new();
        public FeatureRepository Features { get; } = new();
        public BacktestDayProcessor Processor { get; }

        public Fixture() => Processor = new BacktestDayProcessor(
            new BacktestHistoricalMarketDataService(Provider), new MarketObservationAnalyzerFactory(),
            new PortfolioRebalancingService(), new PortfolioAllocationService(), new TradingEngine(),
            new PortfolioPerformanceService(), Snapshots, Features);

        public Task<BacktestDayResult> Day(
            DateTime date, Portfolio portfolio, BacktestExecutionState state, IReadOnlyList<string> symbols,
            StrategyConfiguration? configuration = null) =>
            Processor.ProcessAsync(new BacktestDayRequest
            {
                BacktestRunId = 1, CurrentDate = date, HistoricalStartDate = Friday.AddDays(-4),
                Portfolio = portfolio, ExecutionState = state,
                StrategyConfiguration = configuration ?? Configuration(), Symbols = symbols
            });
    }

    private sealed class Provider : IMarketDataProvider
    {
        public Dictionary<string, List<MarketCandle>> Data { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Symbol, DateTime From, DateTime To)> Fetches { get; } = [];

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from) =>
            throw new InvalidOperationException("Backtests must specify their simulated end date.");

        public Task<IReadOnlyList<MarketCandle>> GetHistoricalDailyAsync(string symbol, DateTime from, DateTime to)
        {
            Fetches.Add((symbol, from, to));
            IReadOnlyList<MarketCandle> candles = Data.TryGetValue(symbol, out var data)
                ? data.Where(x => x.Date >= from && x.Date <= to).ToList() : [];
            return Task.FromResult(candles);
        }
    }

    private sealed class SnapshotRepository : IBacktestSnapshotRepository
    {
        public List<BacktestSnapshot> Rows { get; } = [];
        public Task AddAsync(BacktestSnapshot snapshot) { Rows.Add(snapshot); return Task.CompletedTask; }
        public Task AddRangeAsync(IReadOnlyList<BacktestSnapshot> snapshots)
        {
            Rows.AddRange(snapshots);
            return Task.CompletedTask;
        }
        public Task<List<BacktestSnapshot>> GetByRunIdAsync(int backtestRunId) =>
            Task.FromResult(Rows.Where(x => x.BacktestRunId == backtestRunId).ToList());
    }

    private sealed class FeatureRepository : IFeatureSnapshotRepository
    {
        public List<(int? RunId, string StrategyId, StockRankingFeatures Features)> Rows { get; } = [];
        public Task SaveAsync(IReadOnlyList<ObservationResult> observations,
            StrategyConfiguration strategyConfiguration, int? backtestRunId = null)
        {
            foreach (var observation in observations)
            {
                Assert.NotNull(observation.Features);
                Rows.Add((backtestRunId, strategyConfiguration.Id, observation.Features));
            }
            return Task.CompletedTask;
        }
        public Task<List<FeatureSnapshot>> QueryAsync(DateOnly from, DateOnly to,
            string? symbol = null, string? strategyId = null, int? backtestRunId = null) =>
            throw new NotSupportedException();
    }

    private sealed class RunRepository : IBacktestRunRepository
    {
        public List<BacktestRun> Rows { get; } = [];
        public Task AddAsync(BacktestRun run)
        {
            run.Id = Rows.Count + 1;
            Rows.Add(run);
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(int id) => Task.FromResult(Rows.Any(x => x.Id == id));
        public Task<BacktestRun?> GetByIdAsync(int id) => Task.FromResult(Rows.SingleOrDefault(x => x.Id == id));
        public Task UpdateAsync(BacktestRun run) => Task.CompletedTask;
        public Task MarkFailedAsync(int runId)
        {
            Rows.Single(x => x.Id == runId).Status = BacktestRunStatus.Failed;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProcessor(IBacktestDayProcessor inner) : IBacktestDayProcessor
    {
        public Dictionary<int, BacktestExecutionState> States { get; } = [];

        public Task<BacktestDayResult> ProcessAsync(BacktestDayRequest request)
        {
            if (States.TryGetValue(request.BacktestRunId, out var state))
                Assert.Same(state, request.ExecutionState);
            else
            {
                Assert.Empty(request.ExecutionState.Pending);
                Assert.Empty(request.Portfolio.Trades);
                Assert.Empty(request.Portfolio.Positions);
                States.Add(request.BacktestRunId, request.ExecutionState);
            }
            return inner.ProcessAsync(request);
        }
    }
}
