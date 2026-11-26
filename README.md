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
4. App sorts observations by score descending, then symbol ascending using ordinal comparison. Core's `PortfolioRecommendationService` exposes the full eligible candidate ranking separately from its top-`MaxPositions` recommendation view and derives score-proportional target allocations. Portfolio workflows apply the limit against distinct symbols with positive quantities, so retained holdings occupy slots.
5. `ObservationResultRepository` persists the observation run and the same deterministic rank order. App passes recommendations and prices to `PortfolioWorkflow`, while `ConsoleReporter` presents rankings, recommendations, and portfolio results.

### Backtest flow

1. App loads the selected `StrategyConfiguration`; `BacktestEngineRequestFactory` puts it into the request with dates, capital, and symbols.
2. [BacktestEngine](IntelligentTradingSimulator.App/Simulation/BacktestEngine.cs) persists a running record, creates a separate portfolio, and calls `BacktestDayProcessor` for each calendar date in the inclusive range. This is not an exchange-session calendar.
3. [BacktestDayProcessor](IntelligentTradingSimulator.App/Simulation/BacktestDayProcessor.cs) creates a request-configured analyzer. `BacktestHistoricalMarketDataService` caches requested ranges and restricts returned candles to the simulated date, preventing future candles from entering that day's analysis.
4. At each opening, the day processor executes prior close signals, selling before buying and rechecking the actual position count before every new-symbol purchase. It then marks closes, analyzes the configured universe and held symbols, evaluates holding/protective exits, and creates next-open intents. A missing sale open leaves its slot occupied.
5. The engine carries the portfolio into the next day, then persists final capital, return, portfolio, and completed status. Run-level exceptions mark the run failed; individual symbol-data failures can instead skip that symbol.
6. App's report workflow reads the persisted run. `StrategyVariantRunner` repeats the engine sequentially for configured variants, after which summaries feed the strategy comparison report.

Current boundary: the normal ranking workflow supplies optional fundamentals, but the backtest day processor does not fetch or pass fundamental data. Do not assume current provider values are point-in-time historical inputs.

### Opt-in holding exits

`UseHoldingConditionExit` defaults to `false`. It is a backtest-only opt-in for
controlled strategy experiments, not a universally validated liquidation
policy. When explicitly enabled, one fresh close observation with momentum at
or below 0% signals a full exit at the next strictly later valid opening.
Missing observations do not signal an exit; configured stop-loss and
trailing-stop behavior retains precedence. The threshold and execution scope
are unchanged.

Momentum is measured from the first close to the latest close in the analyzer's
supplied historical window. Thus `MomentumPercent <= 0` means the latest close
is no higher than the first close in that window—not that the stock fell on
one day or is below its purchase price. Strategy effectiveness is unvalidated;
passing software tests does not establish investment performance. See the
[runbook's opt-in A/B evaluation plan](docs/runbook.md#backtest-portfolio-limits)
before using the feature in controlled experiments.

### Benchmark flow

1. App's [BacktestBenchmarkService](IntelligentTradingSimulator.App/Simulation/BacktestBenchmarkService.cs) loads a persisted run's dates and uses `BenchmarkUniverse` defaults unless symbols are supplied explicitly.
2. App's `BenchmarkDataService` retrieves bounded history through `BacktestHistoricalMarketDataService` and computes buy-and-hold returns from the first and last available closes in the range. Symbols without candles are omitted.
3. Core's [BacktestSummaryReportService](IntelligentTradingSimulator.Core/Services/BacktestSummaryReportService.cs) combines those returns with strategy results, classifies outperformance/match/underperformance, and calculates relative return and alpha. Both currently mean strategy return minus benchmark return in percentage points; alpha is not risk-adjusted.
4. Core's `StrategyComparisonReportService` combines variant summaries and optionally ranks by a selected metric, with ordinal strategy-ID tie-breaking. Failed results or missing required benchmark metrics remain unranked.

Benchmark reporting is distinct from relative-strength scoring: the latter supplies benchmark candles to the analyzer before scoring; the former compares completed performance in the report pipeline.

### Risk-management flow

1. Each backtest request carries its own effective strategy configuration, including `MaxPositions`, `MaxPositionWeight`, the holding-exit flag, and optional stop thresholds. The complete configuration is stored on the run and in analyzed feature snapshots.
2. New entries retain the existing eligibility rules (positive score, positive momentum, above SMA5, excluding NVDA). Holding eligibility is less demanding: when enabled, a fresh analyzed close with momentum at or below 0% signals a full exit. That one-close neutral-boundary rule is explicit and is not tuned to historical returns.
3. Missing/insufficient observations do not trigger a holding exit. Stop Loss and Trailing Stop retain their existing opt-in thresholds and take precedence over the holding signal. An exit signal is labeled with its own `ExitReason`; omission from the top-N recommendation view is not a liquidation signal.
4. A close signal executes only at a later valid instrument open. Sells execute before purchases; only a completed full sale frees a slot. New-symbol purchases are ranked for remaining slots, capped by `MaxPositionWeight`, and checked against actual holdings at execution. An over-limit portfolio is not automatically liquidated, and unallocated capital may remain cash.
5. An intent without a valid opening remains deferred and reserves its proposed slot. A fresh close requalifies it; a fresh observation that no longer qualifies or cannot fit cancels it. At a valid open, an intent is consumed once; if a sale did not free a slot, its buy is rejected and only a later fresh qualifying close can create another. Diagnostics identify blocked purchases and exit conditions.
6. `PortfolioWorkflow` applies the same positive-quantity position cap to current portfolio buys. Backtest causal exits use next-open execution; the current portfolio workflow's existing stop behavior remains unchanged.

These stops use supplied prices (daily closes in backtests), not intraday stop-order simulation. Missing prices cannot trigger a stop. Drawdown/recovery metrics are calculated from snapshots for reporting; they are not portfolio-level trading halts. Existing historical runs were produced under the prior recommendation-count policy and are not rewritten or evidence of performance under this lifecycle.

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

### Local model technology decision (issue #204)

**Decision:** train offline with pinned Python and scikit-learn dependencies, export supported estimators and preprocessing as ONNX, and run CPU inference in the .NET 9 application with a pinned ONNX Runtime package. The model bundle is a local directory containing `model.onnx` and `manifest.json`; inference does not download artifacts or send features, labels, or credentials to a service.

| Option | Benefits | Tradeoffs |
| --- | --- | --- |
| **Python/scikit-learn → ONNX → .NET ONNX Runtime (selected)** | Mature Python tools for offline tabular experiments; a portable model and preprocessing graph; no Python environment in the application deployment. [scikit-learn](https://github.com/scikit-learn/scikit-learn/blob/main/COPYING) is BSD-3-Clause and [ONNX Runtime](https://github.com/microsoft/onnxruntime/blob/main/LICENSE) is MIT licensed. | Only estimators and transformations supported by the selected ONNX exporter/runtime can be used. The CPU runtime includes native, platform-specific binaries, increasing deployment size and requiring validation for each supported OS/architecture. |
| ML.NET-native training and model artifacts | One language and runtime for training and inference; [ML.NET](https://github.com/dotnet/machinelearning) is MIT licensed and supports .NET on Windows, Linux, and macOS. | Couples the research/training workflow to .NET and narrows the available Python ecosystem; does not provide the selected ONNX interchange contract. |
| Python-based inference called by the application | Keeps the broad Python model ecosystem and avoids estimator-to-ONNX conversion limits. | Requires a separately installed and maintained Python environment in every application deployment, with process and packaging complexity; weaker fit for the local .NET application. |

Pin the training interpreter, scikit-learn, ONNX exporter, all transitive Python dependencies, and .NET ONNX Runtime package for each released trainer/runtime version. Record the exact versions and a SHA-256 digest of the preserved Python dependency lock in the artifact. Use only CPU inference initially. The supported deployment matrix is the .NET 9 application’s explicitly tested OS/architecture matrix with the corresponding ONNX Runtime CPU native package; do not assume every ONNX operator or platform is supported. Confirm package licenses and native dependencies when pinning or updating versions.

**Artifact and feature contract:** `manifest.json` is a versioned, machine-readable contract, not proof of who produced or approved the bundle. It must include:

- model ID and immutable model version; artifact format/version and SHA-256 of `model.onnx`;
- feature schema version and exact ordered predictor list, types, units, and null policy;
- feature-generation policy, including the benchmark used for relative strength and the history/window policy used for momentum and other window-dependent features;
- training dataset schema/query version and parameters, source data vintage and dataset digest, target/label, horizon, label benchmark, target transformation, and any grouping or filtering rules;
- training and validation cutoffs, including the latest outcome date in each partition; effective trainer parameters, random seeds where applicable, and pinned dependency versions plus the preserved lock-file digest;
- preprocessing operations and fitted parameters, the declared model output/score mapping, and the exact supported .NET and ONNX Runtime versions.

Schema v1’s ordered predictor candidates are `MomentumPercent`, `AverageVolume`, `VolumeTrendPercent`, `VolatilityPercent`, `IsAboveSma5`, `Rsi`, `Atr`, `Week52High`, `DistanceFromWeek52HighPercent`, `RelativeStrengthPercent`, `RevenueGrowthPercent`, `EpsGrowthPercent`, `Margins`, and `Debt`. Required features remain required; unavailable optional values remain missing at the feature boundary and must use the declared preprocessing policy. Fit imputation, scaling, and any missing-value indicators using training rows only, and preserve the fitted preprocessing in the ONNX graph. `Symbol`, `AnalysisDate`, `RelativeStrengthBenchmarkSymbol`, fundamental `AsOfDate`, strategy/configuration metadata, and outcome labels are validation/provenance metadata, not model predictors. Validate each training row and inference request against the manifest’s feature-generation policy; the label benchmark alone does not establish the benchmark used to calculate relative strength. Any change to field meaning, order, units, null handling, or generation policy requires a new compatible schema/policy version or rejection.

Training rows must be partitioned chronologically by outcome availability, not only analysis date; purge overlapping label horizons and keep repeated snapshot rows together. Freeze the candidate after validation. If validation outcomes informed model or hyperparameter selection, final evaluation must start after the validation outcome cutoff and use that later cutoff as the evaluator’s `TrainingCutoff`. Never report the same validation outcomes as untouched held-out results.

Each artifact must predict one declared continuous forward-return target for one horizon: either stock return or benchmark-relative excess return, in percentage points. Invert any declared target transformation and map one predicted percentage point to one ranking point (1:1); this is an ordering score, not a probability or guaranteed return. Apply the configured eligibility rules and quality penalties, then round once to four decimals using midpoint-away-from-zero. Reject an eligible rounded score at or below the reserved `-999` disqualification sentinel rather than clamping it or treating it as disqualified. Higher scores rank first; recommendations continue to require a positive score and allocate according to score magnitude.

**Local loading and failure requirements (future implementation):** a run must select an explicit local model directory through a dedicated application option/configuration. Do not fetch models from a registry or remote service. Validate the manifest schema, hashes, feature contract, and exact supported runtime compatibility before scoring; a hash verifies consistency with the manifest, not authenticity. Missing, malformed, incompatible, or failing artifacts must fail closed—never silently substitute composite scoring or publish partial ranking/backtest results. Inference must depend only on the supplied feature row and local model, and be testable without SQL Server, Yahoo Finance, or network access. Future adapter and workflow regression tests must cover invalid/missing artifacts, feature/provenance mismatches, invalid model outputs, failures after a successful score, and absence of partial results. These are requirements for a separately scoped implementation, not behavior added by this decision.

### Future roadmap (not implemented)

1. **Reproducible external training:** implement the selected pinned Python/scikit-learn-to-ONNX workflow, point-in-time datasets, chronological/horizon-aware splits, training-only preprocessing, and the manifest contract above. There is currently no trainer or model registry.
2. **Local model adapter:** implement `IScoringEngine`, supply it to `AiScoringEngine(scoringAdapter: ...)`, validate the full feature/output contract, and add repeatability, invalid/missing-input, eligibility, tie-breaking, and failure-propagation tests. App factory/model loading would need explicit new wiring; selecting `AI` alone does not supply it.
3. **Evidence-based comparison:** evaluate frozen candidates against the composite baseline over multiple held-out periods, then separately backtest trading/risk outcomes with consistent assumptions. Define review criteria before interpreting results; higher historical metrics alone are not deployment approval.
4. **Optional narrative assistance:** replace explanation interfaces only with evidence-grounded generators that retain limitations and cannot change scores or invent causal claims. LLM integration is not present.
5. **Assisted optimization:** build on versioned tuning research exports, protect independent validation periods, and keep human approval before any parameter promotion. Autonomous strategy changes, production inference, and deployment remain outside the current implementation.

Simulation and comparison results are analytical outputs, not guarantees of real-world investment performance.
