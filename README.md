# IntelligentTradingSimulator

IntelligentTradingSimulator is a .NET 9 application for stock analysis, portfolio simulation, and strategy backtesting.

For local prerequisites, configuration, run commands, and troubleshooting, see the [local runbook](docs/runbook.md).

To define, run, and compare strategy variants, follow the [strategy experimentation flow](docs/runbook.md#strategy-experimentation-flow).

For factor formulas, strategy weights, and score breakdowns, see [advanced ranking](docs/runbook.md#composite-scoring).

## Architecture

This guide describes where the current implementation lives and how its workflows connect. The runbook remains the source for setup, configuration, commands, output formats, and troubleshooting.

### Project structure and responsibilities

| Project / location | Current responsibilities | Where new work belongs |
| --- | --- | --- |
| [Core](IntelligentTradingSimulator.Core) — `Models`, `Enums`, `Interfaces`, `Services` | Domain/configuration models, provider and repository contracts, indicators, scoring and quality rules, recommendations, allocation, trade execution, risk exits, performance and report calculations. | Deterministic analysis, trading policies, metrics, and their contracts. Keep file formats, external APIs, and console concerns out of these rules. |
| [Infrastructure](IntelligentTradingSimulator.Infrastructure) — `MarketData`, `Persistence`, `Migrations`, `Reporting` | Yahoo market data; JSON and FinancialModelingPrep fundamental data providers; EF Core SQL Server context, repositories and migrations; CSV exporters; infrastructure service registration. | External integrations, database mappings/storage, and output serialization. Implement Core contracts rather than embedding scoring or risk policy here. |
| [App](IntelligentTradingSimulator.App) — `Program.cs`, `Configuration`, `Simulation` | Composition root, configuration loading/validation, CLI parsing, stock/benchmark universes, workflow coordination, backtest and variant runners, benchmark data coordination, and console presentation. | Selecting and sequencing services, passing configuration, and user interaction. The backtest engine currently lives here, not in Core. |
| [Tests](IntelligentTradingSimulator.Tests) | xUnit coverage across Core rules, App configuration/workflows, providers, persistence, and CSV reporting, using deterministic fixtures, fakes, and EF Core's in-memory provider where appropriate. | Focused regression tests beside the existing tests for the affected component; avoid live market/network dependencies in unit tests. |

Project references point inward: App references Core and Infrastructure; Infrastructure references Core; Core references neither. Tests reference all three. [Program.cs](IntelligentTradingSimulator.App/Program.cs) wires services together, including Infrastructure's registrations. Some App workflows currently use concrete repositories/exporters; this is a map of the existing structure, not a claim that every boundary is interface-only.

### Ranking flow

1. `SimulationRunner` invokes [MarketDataWorkflow](IntelligentTradingSimulator.App/Simulation/MarketDataWorkflow.cs) for the stock universe and selected strategy window.
2. Infrastructure providers supply candles and optional fundamentals. App orders and bounds history, skips symbols with insufficient candles, and supplies benchmark history when relative strength is enabled.
3. Core's [MarketObservationAnalyzer](IntelligentTradingSimulator.Core/Services/MarketObservationAnalyzer.cs) computes indicators and delegates scoring to `IScoringEngine` (the application currently selects `CompositeScoringEngine`). Scoring/quality decisions and factor contributions belong in Core, not the provider or reporter.
4. App sorts observations by score descending, then symbol ascending using ordinal comparison. Core's `PortfolioRecommendationService` applies eligibility and position-count rules and derives score-proportional target allocations. Recommendations are a filtered subset, not the complete ranking.
5. `ObservationResultRepository` persists the observation run and the same deterministic rank order. App passes recommendations and prices to `PortfolioWorkflow`, while `ConsoleReporter` presents rankings, recommendations, and portfolio results.

### Backtest flow

1. App loads the selected `StrategyConfiguration`; `BacktestEngineRequestFactory` puts it into the request with dates, capital, and symbols.
2. [BacktestEngine](IntelligentTradingSimulator.App/Simulation/BacktestEngine.cs) persists a running record, creates a separate portfolio, and calls `BacktestDayProcessor` for each calendar date in the inclusive range. This is not an exchange-session calendar.
3. [BacktestDayProcessor](IntelligentTradingSimulator.App/Simulation/BacktestDayProcessor.cs) creates a request-configured analyzer. `BacktestHistoricalMarketDataService` caches requested ranges and restricts returned candles to the simulated date, preventing future candles from entering that day's analysis.
4. The day processor analyzes available symbols, ranks them, builds recommendations, updates position prices, and invokes Core rebalancing/trading rules. It persists a daily value/cash/performance/trade-count snapshot.
5. The engine carries the portfolio into the next day, then persists final capital, return, portfolio, and completed status. Run-level exceptions mark the run failed; individual symbol-data failures can instead skip that symbol.
6. App's report workflow reads the persisted run. `StrategyVariantRunner` repeats the engine sequentially for configured variants, after which summaries feed the strategy comparison report.

Current boundary: the normal ranking workflow supplies optional fundamentals, but the backtest day processor does not fetch or pass fundamental data. Do not assume current provider values are point-in-time historical inputs.

### Benchmark flow

1. App's [BacktestBenchmarkService](IntelligentTradingSimulator.App/Simulation/BacktestBenchmarkService.cs) loads a persisted run's dates and uses `BenchmarkUniverse` defaults unless symbols are supplied explicitly.
2. App's `BenchmarkDataService` retrieves bounded history through `BacktestHistoricalMarketDataService` and computes buy-and-hold returns from the first and last available closes in the range. Symbols without candles are omitted.
3. Core's [BacktestSummaryReportService](IntelligentTradingSimulator.Core/Services/BacktestSummaryReportService.cs) combines those returns with strategy results, classifies outperformance/match/underperformance, and calculates relative return and alpha. Both currently mean strategy return minus benchmark return in percentage points; alpha is not risk-adjusted.
4. Core's `StrategyComparisonReportService` combines variant summaries and optionally ranks by a selected metric, with ordinal strategy-ID tie-breaking. Failed results or missing required benchmark metrics remain unranked.

Benchmark reporting is distinct from relative-strength scoring: the latter supplies benchmark candles to the analyzer before scoring; the former compares completed performance in the report pipeline.

### Risk-management flow

1. In backtests, each request supplies its variant's stop-loss and trailing-stop percentages to rebalancing. Recommendation and allocation services are singletons initialized from the active strategy configuration, so variant-specific `MaxPositions` and `MaxPositionWeight` values do not take effect.
2. Core recommendation/allocation services limit selection and target exposure. [PortfolioRebalancingService](IntelligentTradingSimulator.Core/Services/PortfolioRebalancingService.cs) evaluates enabled position stops before normal allocation changes.
3. With an available positive price, rebalancing updates the position's highest price since entry, checks the fixed stop against average entry price, then checks the trailing stop against that high-water mark. A fixed stop takes precedence if both trigger. Triggered positions are sold and excluded from recommendations for that execution, preventing immediate repurchase.
4. `TradingEngine` updates cash, positions, and trade history, including exit reasons. Rebalancing then performs ordinary allocation sells before buys.
5. App's `PortfolioWorkflow` persists portfolio/position snapshots; the backtest day processor persists daily backtest snapshots and includes held symbols in price collection when stops are enabled, even if they are outside the requested universe.

These stops use supplied prices (daily closes in backtests), not intraday stop-order simulation. Missing prices cannot trigger a stop. Drawdown/recovery metrics are calculated from snapshots for reporting; they are not portfolio-level trading halts. New exit policies belong in Core, with App supplying inputs and Infrastructure persisting any required state.

### Export and reporting flow

1. App's [BacktestReportWorkflow](IntelligentTradingSimulator.App/Simulation/BacktestReportWorkflow.cs) loads an existing run for reporting, whether reached after execution or by run ID. Core's `BacktestSummaryReportService` reads run/snapshot contracts and calculates drawdown, recovery, activity totals, and benchmark comparisons.
2. App's `ConsoleReporter` presents results. Infrastructure's [Reporting](IntelligentTradingSimulator.Infrastructure/Reporting) exporters serialize summary, daily snapshot, and strategy comparison CSVs; metric definitions remain in Core.
3. Summary CSV export requires a successful report for a completed run. Snapshot CSV export reads persisted snapshots independently and does not require completion; a snapshot-only request returns without generating a summary.
4. For variants, App's `StrategyComparisonReportWorkflow` passes summaries to Core's comparison service, then prints the report and optionally exports it through `StrategyComparisonCsvExporter`.

Report generation can fetch benchmark market data; it is not necessarily an offline read of stored results. Keep new report calculations in Core, export formatting/file I/O in Infrastructure, and command routing/presentation in App. See the [runbook's output guide](docs/runbook.md#benchmarks-and-output) for options and CSV schemas.

### Contributor change guide

- **New indicator, factor, quality filter, or risk rule:** add the model/configuration contract and calculation in Core; extend App configuration validation and workflow inputs as needed; add provider/storage support in Infrastructure only when required.
- **New data source or persisted state:** implement the relevant Core interface in Infrastructure, update EF mappings/migrations if needed, and register it at the composition boundary.
- **New execution mode or workflow:** coordinate existing services in App rather than copying calculations into the runner.
- **New metric or export column:** calculate the value once in Core, carry it in the report model, then update App presentation and Infrastructure serialization consistently.
- **Tests and documentation:** add deterministic tests for changed behavior in Tests and update this architecture map when ownership or sequencing changes. Keep runnable examples and operational details in the [local runbook](docs/runbook.md).

Simulation and comparison results are analytical outputs, not guarantees of real-world investment performance.
