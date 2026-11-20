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
5. **Evaluate a frozen candidate offline.** Follow the [evaluation API example](docs/runbook.md#offline-model-evaluation). Choose one strategy, horizon, and benchmark; start evaluation strictly after all outcomes used to fit or select the candidate and end by `OutcomeCutoff`. If validation influenced model selection or tuning, pass its outcome cutoff as the evaluator's `TrainingCutoff`, not the earlier training-data cutoff. The default AI placeholder should match an identically configured composite baseline: this is a useful integration check, not evidence of AI improvement.
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

### Local training and inference stack (decision; not implemented)

Use **Python with pinned scikit-learn and `skl2onnx` versions for offline training**, and export one ONNX model for **local CPU inference through a pinned `Microsoft.ML.OnnxRuntime` package in the .NET 9 application**. Training consumes the existing versioned JSON dataset export; Python is not shipped with the application. This keeps the training toolchain separate from the C# runtime while using a portable artifact format. ONNX Runtime adds native, platform-specific binaries and conversion/operator compatibility must be checked for every model. The repository currently targets `net9.0` without declaring supported operating systems, so each application release must pin and test its runtime package version and deployment targets.

| Option | Tradeoffs for this repository |
| --- | --- |
| **Python/scikit-learn → ONNX (selected)** | Fits the numeric, tabular `StockRankingFeatures` and existing JSON export; ONNX separates training from .NET inference and avoids bundling Python. Requires conversion-compatible preprocessing/operators and ships ONNX Runtime native binaries. scikit-learn is BSD-3-Clause and ONNX Runtime is MIT licensed ([licenses](https://github.com/scikit-learn/scikit-learn/blob/main/COPYING), [runtime license](https://github.com/microsoft/onnxruntime/blob/main/LICENSE)). |
| **ML.NET-native model** | A `.zip` model is straightforward to save and load in .NET and keeps training and inference in the same ecosystem, but ties the training pipeline and artifact to ML.NET. Its model conversion to ONNX is also subject to trainer/transformer support. ML.NET is MIT licensed ([model persistence](https://learn.microsoft.com/en-us/dotnet/machine-learning/how-to-guides/save-load-machine-learning-models-ml-net), [license](https://github.com/dotnet/machinelearning/blob/main/LICENSE)). |
| **Python at inference time** | Reuses the training environment directly, but requires installing and managing Python and its dependencies on every .NET host; it is not selected. |

#### Artifact and compatibility contract

Supply a local artifact directory explicitly with `--model-path=<directory>`. It contains `model.onnx`, `manifest.json`, and the preserved training dependency lock `training.lock`; there is no model registry, download, or remote inference. The manifest is part of the artifact contract and records:

- `modelId`, `modelVersion`, artifact/manifest format version, model-file SHA-256, ONNX opset, and input/output tensor names, types, and shapes;
- `featureSchemaVersion` and `datasetSchemaVersion`, both `1` for the current `StockRankingFeatures` and `TrainingDataset` schemas, plus the exact feature mapping;
- source dataset SHA-256, serialized `TrainingDatasetQuery`, training-data vintage and row count, trainer source revision, exact Python/trainer/converter versions, and random seed when applicable; do not embed source rows, credentials, or connection strings;
- the selected target field, label horizon and label benchmark, target transformation and fitted parameters (including identity when unchanged), effective trainer algorithm and all settings including defaults, seed, and execution environment. Preserve a complete exact transitive dependency lock (including numerical libraries and platform-specific distributions), its format, platform/Python details and SHA-256; verify the lock hash and use the preserved lock to recreate the training environment rather than resolving top-level pins again;
- feature-generation policy and source revision: strategy-window definition and effective lookback (including calendar/trading-session convention and endpoints), 52-week history policy, relative-strength enablement and feature benchmark, and missing-data policy. The label benchmark in `TrainingDatasetQuery.BenchmarkSymbol` is separate and cannot establish the feature benchmark;
- training and validation cutoffs with the cutoff convention. A training row's `OutcomeDate` must not exceed the training cutoff; validation rows start after that cutoff, purge overlapping label horizons, and record their own outcome cutoff. Record whether validation influenced selection or tuning and the resulting model-freeze cutoff;
- preprocessing version and policy, plus the exact supported and tested `Microsoft.ML.OnnxRuntime` package version. The host pins the package version; floating versions are not supported.

Training must validate every row against the recorded feature policy, using `Features.RelativeStrengthBenchmarkSymbol` and retained strategy configuration (`IncludeStrategyMetadata`) plus independently retained provenance for the actual history window and generation policy. Configuration alone does not prove which candles were used. At inference, the caller must supply equivalent generation metadata alongside each feature row; validate it and the row's feature benchmark against the manifest before scoring. Reject absent, unverifiable, or mismatched metadata rather than assuming a schema match proves semantic compatibility. Null relative strength is allowed only under the recorded missing-data policy; a disabled benchmark must not be confused with an unavailable enabled benchmark. The current dataset and scoring interfaces do not carry all required provenance, so future training and adapter work must provide it; metadata is for validation, not prediction.

For schema version 1, the ordered model inputs are `MomentumPercent`, `AverageVolume`, `VolumeTrendPercent`, `VolatilityPercent`, `IsAboveSma5`, `Rsi`, `Atr`, `Week52High`, `DistanceFromWeek52HighPercent`, `RelativeStrengthPercent`, and `Fundamentals.RevenueGrowthPercent`, `Fundamentals.EpsGrowthPercent`, `Fundamentals.Margins`, `Fundamentals.Debt`. Convert these to `float32`, encode `IsAboveSma5` as 0/1, and represent unavailable optional metrics (including absent fundamentals) as NaN. Store imputation, an explicit missingness indicator for each nullable metric, scaling/encoding, and all fitted preprocessing parameters in the ONNX graph; fit them on training data only and identify their version/policy in the manifest. Do not feed `Symbol`, `AnalysisDate`, benchmark symbols, `Fundamentals.Symbol`, or `Fundamentals.AsOfDate` as predictors. The model returns one finite scalar per row. Labels and future outcomes remain training/evaluation targets only and never enter inference inputs.

Final evaluation must start strictly after the model-freeze cutoff, which is the latest outcome date used for fitting, preprocessing, selection or tuning; purge overlapping label windows at each boundary. When validation influenced selection, use its outcome cutoff as the evaluator's `TrainingCutoff` (or a later freeze cutoff if applicable). The evaluator only enforces the cutoff supplied by the caller; it cannot detect reuse of validation outcomes.

The adapter must apply the manifest's versioned score and eligibility policy, not pass raw model outputs through. For this initial policy, map a finite raw scalar `x` to `1 + 99 / (1 + exp(-x))` using a numerically stable sigmoid, then convert to a representable decimal; record the formula, conversion/rounding and version in the manifest. This fixed, monotonic mapping is positive and preserves ordering except for numerical ties; it is not a predicted return or a calibrated probability. Apply the configured composite eligibility rules before scoring: disqualify rows not above SMA5, and when `UseQualityFilter` is enabled disqualify volatility above 3% or quality-filter exclusions using the configured thresholds and missing/failure actions. For a quality `Penalize` action, subtract the configured `QualityPenalty` once from the mapped score; `Neutral` leaves it unchanged. Record the effective eligibility configuration in the manifest and reject mismatches at inference. `AiScoringEngine` does not enforce these rules for a supplied adapter.

Return `-999` only for an explicitly disqualified row; reject non-finite, unrepresentable, or eligible mapped scores equal to that reserved sentinel instead of silently colliding with it. Scores after any penalty may be zero or negative: the evaluator includes them and compares their sign with return sign, whereas recommendations require positive score, positive momentum, above-SMA5 and a symbol other than NVDA, then allocate proportionally to score magnitude among selected candidates. Thus the mapping and penalty intentionally affect allocation as well as ordering; sign accuracy is not a calibrated directional forecast, and evaluator top selections are not identical to recommendations. Validate the mapping, penalties, sentinel and these consumer differences in offline adapter tests before use.

The loader requires an exact supported feature schema and runtime version, verifies the manifest, model and lock hashes, and validates the input/output contract. Missing, malformed, corrupt, incompatible, or non-finite model artifacts or outputs fail explicitly; selecting AI must never silently fall back to composite scoring. Inference consumes an already-built feature row with its generation metadata and reads only the local artifact, so adapter and contract tests need neither SQL Server nor Yahoo Finance nor network access. No credentials or private market/portfolio data are sent to a third party.

### Future roadmap (not implemented)

1. **Reproducible external training:** implement point-in-time splits, training-only preprocessing, and versioned ONNX artifact generation under the decision above. There is currently no trainer or model registry.
2. **Local model adapter:** implement `IScoringEngine` over ONNX Runtime, supply it to `AiScoringEngine(scoringAdapter: ...)`, and wire explicit artifact loading in the App. Validate the full feature/output contract and add repeatability, invalid/missing-input, eligibility, tie-breaking, and failure-propagation tests; selecting `AI` alone still does not supply a model.
3. **Evidence-based comparison:** evaluate frozen candidates against the composite baseline over multiple held-out periods, then separately backtest trading/risk outcomes with consistent assumptions. Define review criteria before interpreting results; higher historical metrics alone are not deployment approval.
4. **Optional narrative assistance:** replace explanation interfaces only with evidence-grounded generators that retain limitations and cannot change scores or invent causal claims. LLM integration is not present.
5. **Assisted optimization:** build on versioned tuning research exports, protect independent validation periods, and keep human approval before any parameter promotion. Autonomous strategy changes, production inference, and deployment remain outside the current implementation.

Simulation and comparison results are analytical outputs, not guarantees of real-world investment performance.
