# IntelligentTradingSimulator

IntelligentTradingSimulator is a .NET 9 application for stock analysis, portfolio simulation, and strategy backtesting.

For local prerequisites, configuration, run commands, and troubleshooting, see the [local runbook](docs/runbook.md).

To define, run, and compare strategy variants, follow the [strategy experimentation flow](docs/runbook.md#strategy-experimentation-flow).

For factor formulas, strategy weights, and score breakdowns, see [advanced ranking](docs/runbook.md#composite-scoring).

For implemented AI integration points, offline research workflows, and the future evolution path, see [AI-ready architecture and workflows](#ai-ready-architecture-and-workflows).

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
3. Core's [MarketObservationAnalyzer](IntelligentTradingSimulator.Core/Services/MarketObservationAnalyzer.cs) computes indicators and delegates scoring to `IScoringEngine`. `ScoringEngineFactory` selects the strategy's `Composite` (default), `RuleBased`, or `AI` implementation for both ranking and backtesting. Scoring/quality decisions and factor contributions belong in Core, not the provider or reporter.
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

## AI-ready architecture and workflows

### Implemented today: integration points, not a trained AI system

The foundations delivered by issues [#53](https://github.com/ExarKun1983/IntelligentTradingSimulator/issues/53)–[#58](https://github.com/ExarKun1983/IntelligentTradingSimulator/issues/58) support explanations, engine selection, offline evaluation, and tuning research. Feature snapshots, future-outcome labels, and dataset export provide the preceding data pipeline. None of these components trains or deploys a model, invokes an LLM, or performs network model inference.

| Boundary | Current implementation | Extension point / limitation |
| --- | --- | --- |
| Scoring | Core's [IScoringEngine](IntelligentTradingSimulator.Core/Interfaces/IScoringEngine.cs) accepts `ScoringContext` and returns a decimal score with optional applied diagnostics. [ScoringEngineFactory](IntelligentTradingSimulator.Core/Services/ScoringEngineFactory.cs) selects by `StrategyConfiguration.ScoringEngine`; App validates names and uses the same selection for ranking and backtests. | Unsupported names fail clearly. Engines without diagnostics return null, not invented factor contributions. |
| Feature inputs | Core's [StockRankingFeatures](IntelligentTradingSimulator.Core/Models/StockRankingFeatures.cs) represents unweighted inputs for one symbol/date. `MarketObservationAnalyzer` exposes it through `ObservationResult.Features` and `ScoringContext.Features`. | Optional metrics remain null; legacy callers can omit the entire row. Feature availability does not imply that a scoring factor is enabled. |
| Historical storage | App analysis/backtest workflows save `FeatureSnapshot` rows through Infrastructure persistence, retaining feature/configuration JSON, optional score/diagnostics, schema version, and strategy/run identity. | First snapshot per identity is retained; use distinct strategy IDs for distinct configurations. Capturing inputs does not create labels. |
| Offline datasets | Infrastructure's `FutureOutcomeLabelWorkflow` creates separate `FutureOutcomeLabel` targets. Core's [TrainingDatasetBuilder](IntelligentTradingSimulator.Core/Training/TrainingDatasetBuilder.cs) joins stored features/labels; Infrastructure exports versioned JSON. | Explicit library jobs, not scoring calls or CLI modes; no in-process training. Date filters select analysis dates, not necessarily outcome dates. |
| Ranking explanations | Core's `IStockRankingExplanationService` / `StockRankingExplanationService` uses actual inputs, applied contributions, and diagnostic statuses; App prints the narrative. | Template-based, not LLM-generated. Missing breakdowns mean unknown causes; optional previous observations require comparable contexts supplied by the caller. |
| Result explanations | Core's `IResultExplanationService` / `ResultExplanationService` explains backtest/comparison metrics in console reports. | Aggregate return, benchmark, and risk evidence cannot establish holding-level or factor-level causes of portfolio returns. |
| Offline evaluation | Core's [OfflineModelEvaluationService](IntelligentTradingSimulator.Core/Training/OfflineModelEvaluationService.cs) scores held-out features through `AiScoringEngine` and a deterministic composite baseline; Infrastructure exports the report. | No model provenance verification, training, deployment decision, or market-data retrieval during evaluation. |
| Tuning research | Core's `IStrategyTuningAnalysisService` / `StrategyTuningAnalysisService` screens historical summaries and supplied parameter snapshots; App supports offline JSON input/output. | Deterministic `heuristic-v1` decision support, not autonomous optimization or parameter deployment. |

#### Deterministic scoring versus future model scoring

- `Composite` applies configured decimal factor weights, eligibility gates, and quality policies. `RuleBased` currently delegates to that same engine; it is not a different predictive baseline.
- `AI` currently selects [AiScoringEngine](IntelligentTradingSimulator.Core/Services/AiScoringEngine.cs), a deliberately non-production wrapper that also delegates to composite scoring by default. With identical features/configuration, all three selections therefore produce the same scores and diagnostics. Selecting `AI` does **not** demonstrate predictive ability.
- The wrapper's optional `scoringAdapter` constructor argument accepts another `IScoringEngine`. A future model adapter can replace the calculation without replacing analysis or ranking, but must define its feature preprocessing, missing-input policy, model version, eligibility behavior, and output meaning. The factory does not currently load such an adapter or model artifact from configuration.
- Higher scores rank first, with ordinal symbol tie-breaking. `StrategyConfiguration.DisqualifiedScore` (`-999`) denotes ineligibility, not a low-confidence prediction. Scores are ranking points, not probabilities or promised returns. Adapter errors propagate; the wrapper does not silently fall back.
- Decimal inputs cannot represent NaN/infinity and arithmetic overflow is surfaced. A future floating-point model adapter must explicitly validate and convert outputs rather than silently substituting zero or the disqualification sentinel. Applied diagnostics must describe the calculation actually used, not borrow composite explanations for a different model.

For configuration syntax and formulas, see [scoring engine selection and composite scoring](docs/runbook.md#composite-scoring).

### Data flow and workflow separation

```text
Ranking / backtest path (no future labels)
  Infrastructure candles + optional current fundamentals
    -> App bounds/orders history for the analysis date
    -> Core MarketObservationAnalyzer -> StockRankingFeatures
    -> ScoringContext -> selected IScoringEngine
    -> ObservationResult (score + optional applied factor diagnostics)
       -> deterministic ranking -> recommendations / simulated trading
       -> template ranking explanations
       -> persisted FeatureSnapshot (inputs + configuration + diagnostics)

Explicit offline research path
  FeatureSnapshot + bounded historical prices
    -> FutureOutcomeLabelWorkflow -> separate FutureOutcomeLabel rows
    -> TrainingDatasetBuilder -> versioned training-dataset JSON
       -> future external training on a chronological training partition
       -> OfflineModelEvaluationService on a held-out partition
          -> features only -> fixed AI adapter + composite baseline
          -> labels only -> metric calculation -> evaluation JSON

Historical backtest summaries + actual historical parameter snapshots
  -> strategy comparison / result explanations
  -> offline tuning analysis -> console / research JSON -> human review
```

Training and evaluation are separate from live ranking. Outcome labels must never enter `StockRankingFeatures`, `ScoringContext`, or an inference adapter. Ranking explanations describe score evidence; result explanations describe aggregate outcomes; tuning highlights research candidates. None proves that a factor or parameter caused a realized return.

### Practical developer workflow

1. **Establish a deterministic baseline.** Configure a named strategy with `"ScoringEngine": "Composite"` and preserve its configuration, universe, dates, and data vintage. Switching the same strategy to `"ScoringEngine": "AI"` exercises the placeholder boundary only. Run ranking/backtests using the [runbook commands](docs/runbook.md#strategy-experimentation-flow); normal application runs require SQL Server and market-data access.
2. **Capture historical features.** Apply the existing migrations, then collect [daily feature snapshots](docs/runbook.md#daily-feature-snapshots) for all successfully analyzed stocks, not only selected recommendations. Retain the snapshot identity/schema and configuration; failed or insufficient-history analyses have no snapshot.
3. **Generate targets offline.** In a separate scoped job, call `FutureOutcomeLabelWorkflow.GenerateAsync` with an explicit `availableThrough` date. [Labels](docs/runbook.md#offline-supervised-training-labels) use exact snapshot-date closes and subsequent stock trading sessions (defaults: 5, 20, 60). Immature or invalid stock endpoints produce no label; missing benchmark endpoints leave benchmark targets null. Generation does not update scoring inputs.
4. **Build and preserve the dataset.** Use the [dataset builder/export example](docs/runbook.md#offline-dataset-export), choosing strategy/run, analysis-date range, horizon, and benchmark. Preserve the exported version-1 schema/query and source data vintage. Repeated horizons/benchmarks repeat feature rows; do not randomly split those copies across training and evaluation.
5. **Evaluate a frozen candidate offline.** Follow the [evaluation API example](docs/runbook.md#offline-model-evaluation). Choose one strategy, horizon, and benchmark; start evaluation strictly after `TrainingCutoff` and end by `OutcomeCutoff`. The default AI placeholder should match an identically configured composite baseline: this is a useful integration check, not evidence of AI improvement.
6. **Review metrics and limitations.** Compare eligible counts, sign-based directional accuracy, mean per-date/run Spearman rank correlation, and average forward return of top selections. Ties use average ranks for correlation and ordinal symbols for selection. Disqualification is applied separately per engine, so inspect eligibility differences as well as scores. Empty metrics are null. Score-sign accuracy is only a diagnostic; top-selection mean return is not a compounded portfolio backtest.
7. **Research variants with human oversight.** Use [offline strategy tuning](docs/runbook.md#offline-strategy-tuning-research) with at least two historical runs and their actual parameter snapshots. The database-free CLI accepts `--tuning-input=/tmp/tuning-request.json` and optionally `--export-tuning-json=/tmp/tuning-research.json`; the runbook includes a complete sample. Review strengths, drawdown warnings, missing evidence, and promising-candidate criteria before choosing further experiments. It never changes strategy parameters.

### Validation, leakage, and safety boundaries

**Implemented safeguards**

- Historical candle retrieval bounds backtest inputs to the simulated date. Backtests do not fetch today's fundamentals; normal analysis discards fundamentals with a known reporting date after the analysis date.
- Evaluation validates selected rows before calling either scorer: supported schemas, matching feature date/symbol, forward outcome dates, no future-dated fundamentals, and no duplicate symbols within a date/run cohort. Outcomes beyond the explicit outcome cutoff are excluded and counted; labels never reach either scorer.
- Optional missing features, benchmark targets, unavailable diagnostics, and empty evaluation metrics remain distinguishable from zero. Explanations preserve disabled/unavailable/disqualified states and do not invent missing attribution.
- Tuning checks compatible completed-run dates, initial capital, benchmark evidence, and configuration identity; failed/incomplete runs stay visible without becoming ranked candidates. It is research-only, not production deployment.

**Caller responsibilities and remaining limits**

- A training row is safe only if its **outcome date**, not just its analysis date, is on or before the training cutoff. Purge overlapping label windows at chronological boundaries; keep repeated snapshots together and reserve untouched hold-out periods for final assessment. Fit preprocessing only on training data. These are research obligations, not a built-in training/splitting pipeline.
- A reporting-period date is not a publication/availability timestamp. Unknown publication timing, revised history, survivorship bias in the chosen universe, or upstream feature leakage cannot be detected or repaired by the evaluator. Supply point-in-time data and retain a fixed vintage; do not treat present-day fundamentals as historical facts.
- Freeze and record model artifact/version, preprocessing, training-data provenance, seeds if applicable, strategy configuration, cutoffs, and evaluation exports. `ModelId` identifies a caller-supplied candidate; it does not prove how that candidate was trained. Re-running a database query can include newly added snapshots or labels.
- Keep raw labels, returns, stored deterministic scores, and optional strategy metadata out of predictive feature inputs. Compare like-for-like periods, universes, costs, and data provenance; repeated tuning on the hold-out period invalidates it as independent evidence. No automatic acceptance threshold or deployment gate is implemented.
- Keep API keys, database credentials/connection strings, and any future inference secrets out of source, datasets, model artifacts, prompts, reports, and logs. Use only placeholders in examples and local configuration as described in the runbook; the current application does not automatically load environment variables as configuration. Do not send data or credential-bearing provider URLs to external AI services. Any future remote integration needs an explicit data/privacy and secret-handling design first.

Existing deterministic coverage includes `AiScoringEngineTests`, `StockRankingFeaturesTests`, `FeatureSnapshotPersistenceTests`, `FutureOutcomeLabelTests`, `TrainingDatasetTests`, `StockRankingExplanationServiceTests`, `ResultExplanationServiceTests`, `OfflineModelEvaluationTests`, and strategy-tuning service/workflow tests. Use the SDK from `global.json` and the [complete restore/build/test commands](docs/runbook.md#verify-local-setup) when changing these boundaries. Add focused tests for changed behavior; this documentation introduces no runtime behavior.

### Future roadmap (not implemented)

1. **Reproducible external training:** establish point-in-time datasets, chronological/horizon-aware splits, training-only preprocessing, and versioned model artifacts. There is currently no trainer or model registry.
2. **Local model adapter:** implement `IScoringEngine`, supply it to `AiScoringEngine(scoringAdapter: ...)`, validate the full feature/output contract, and add repeatability, invalid/missing-input, eligibility, tie-breaking, and failure-propagation tests. App factory/model loading would need explicit new wiring; selecting `AI` alone does not supply it.
3. **Evidence-based comparison:** evaluate frozen candidates against the composite baseline over multiple held-out periods, then separately backtest trading/risk outcomes with consistent assumptions. Define review criteria before interpreting results; higher historical metrics alone are not deployment approval.
4. **Optional narrative assistance:** replace explanation interfaces only with evidence-grounded generators that retain limitations and cannot change scores or invent causal claims. LLM integration is not present.
5. **Assisted optimization:** build on versioned tuning research exports, protect independent validation periods, and keep human approval before any parameter promotion. Autonomous strategy changes, production inference, and deployment remain outside the current implementation.

Simulation and comparison results are analytical outputs, not guarantees of real-world investment performance.
