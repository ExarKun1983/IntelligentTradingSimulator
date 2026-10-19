# Local development runbook

This guide covers the current console application and its SQL Server and Yahoo Finance dependencies. Backtests reuse persisted historical candles; missing ranges and the regular simulation require Yahoo Finance access.

For the connected feature-to-dataset-to-evaluation flow, implemented AI boundaries, safety constraints, and explicitly future roadmap, see the [AI-ready architecture and workflows](../README.md#ai-ready-architecture-and-workflows). The API and command examples below describe today's implementation; selecting the `AI` engine does not load a trained model.

## Console workflow progress

Progress is enabled by default for simulations, backtests, variant runs, and report/export
commands. Add `--no-progress` to suppress progress lines while retaining the final console
reports, warnings, and export confirmations:

```bash
dotnet run --project IntelligentTradingSimulator.App -- --run-backtest --no-progress
```

Progress uses plain text suitable for terminals and redirected logs. Routine updates are
throttled; new stages and forced status messages are announced immediately. During a slow
operation, a heartbeat is emitted about every five seconds, including elapsed time and the
current stage. Available context includes strategy, run, variant, date, and symbol counts.
A heartbeat means the command is still waiting or working, not a guarantee that an external
provider or database is responsive.

Progress output is best-effort: if its writer reports an I/O error or is disposed,
further progress output and heartbeats stop without interrupting data retrieval,
financial calculations, or persistence.

Backtest date percentages count inclusive calendar dates, including weekends, rather than
trading sessions. Simulation completion is separate from command completion: summaries,
benchmark calculations, and CSV exports may still be running after the simulation reaches
100%. The final command-completion message follows all requested reporting and exports;
an export is only confirmed after its write succeeds. Snapshot-only report exports do not
generate summaries or request benchmarks.

## Prerequisites

- The .NET SDK version in `global.json` (currently `9.0.318`; its `latestPatch` roll-forward setting permits later patches in that feature band).
- SQL Server reachable over TCP at the server and port in `ConnectionStrings:TradingDb`. The checked-in configuration uses `localhost,1433`.
- Network access to Yahoo Finance for application runs.
- The `dotnet-ef` tool to apply database migrations.

## Set up the database and application

From the repository root, restore the solution and build it in Release configuration:

```bash
dotnet restore IntelligentTradingSimulator.sln
dotnet build IntelligentTradingSimulator.sln --configuration Release --no-restore
```

Install the EF Core command-line tool matching the repository's EF Core version:

```bash
dotnet tool install --global dotnet-ef --version 9.0.17
```

The application does not apply migrations automatically. Set `TRADING_DB_CONNECTION` to a SQL Server connection string, then apply the migrations:

```bash
export TRADING_DB_CONNECTION='Server=localhost,1433;Database=IntelligentTradingSimulator;User Id=SQL_LOGIN;******;TrustServerCertificate=True;'
dotnet ef database update \
  --project IntelligentTradingSimulator.Infrastructure \
  --startup-project IntelligentTradingSimulator.App
```

The infrastructure design-time context factory requires `TRADING_DB_CONNECTION`. Set it to the same database used by the application. This variable is for EF migrations; the application itself reads `ConnectionStrings:TradingDb` from `IntelligentTradingSimulator.App/appsettings.json` and does not load environment variables as configuration.

In `IntelligentTradingSimulator.App/appsettings.json`, set `ConnectionStrings:TradingDb` to your local SQL Server connection string. For example, retain the existing server/database/options and replace the credentials with your own:

```json
{
  "ConnectionStrings": {
    "TradingDb": "Server=localhost,1433;Database=IntelligentTradingSimulator;User Id=SQL_LOGIN;******;TrustServerCertificate=True;"
  }
}
```

Use a SQL Server login with permission to create and update the application's database schema. The app project copies `appsettings.json` to its output directory. Keep real credentials local; do not commit them.

## Persistent historical market data

Apply the `AddMarketCandles` migration before running backtests or synchronization.
Preload an inclusive range without starting a backtest:

```bash
dotnet run --project IntelligentTradingSimulator.App -- --sync-market-data --from=2019-01-01 --to=2024-12-31
```

By default this loads the stock universe and SPY/QQQ/IWM benchmarks. Restrict it with
`--symbols=AAPL,MSFT,SPY`. Both dates are required, must be ordered, and must precede
today (UTC). `--no-progress` works as for other workflows. Synchronization exits
after loading; run the backtest or variants command separately against the same
database. Include the strategy lookback and 52-week history before the backtest
start (the analyzer can require 504 calendar days), as well as all held symbols
and benchmarks, to avoid provider calls during a backtest.

`MarketCandles` stores normalized symbols, daily OHLCV, `Source=YahooFinance`,
`PriceAdjustmentMode=YahooOhlc`, and UTC `DownloadedAt`. `YahooOhlc` means the
provider's Open/High/Low/Close fields, **not** its dividend-adjusted AdjustedClose.
No additional adjustment is performed. Yahoo may incorporate corporate-action
adjustments into OHLC; these are provider-reported values, not guaranteed
point-in-time unadjusted prices. An incompatible adjustment convention for the
same source/date is rejected, never silently mixed.

The unique `(Symbol, Source, Date)` index prevents duplicate candles. Bulk upsert
is **first download wins**: repeat/overlapping downloads retain existing OHLCV and
timestamps so strategy variants and later processes reuse identical stored input.
Covered empty dates are preserved too: a delayed overlapping response cannot fill
a gap already certified by an earlier download. SQL Server writes take a
transaction-owned application lock per source/symbol to coordinate separate
processes before reading/inserting the snapshot.
The database is the snapshot; retain a database backup for reproducibility across
database resets. This is not a versioned corporate-action or as-of-data service.
Today's incomplete candles and future candles are not persisted or marked covered.

`MarketDataCoverage` records successfully fetched inclusive intervals, including
empty weekends/holidays, in the same transaction as the candles. Retrieval reads
the database first, subtracts covered intervals and already stored candle dates,
and requests only the remaining calendar gaps. The provider contract classifies a
returned candle list as success and an empty list as **confirmed no data**.
Failures, null responses, and invalid OHLCV are not covered.
Unexpected provider omissions within a successful response therefore require an
explicit database repair; there is no automatic refresh of stored snapshots.
The Yahoo client uses the same "Requested Information Not Available On Yahoo Finance"
exception for empty charts and HTTP 404, so that message alone is **not** proof of
no data. For that ambiguous exception on a bounded request, the adapter makes one
additional chart request for the identical UTC range (Yahoo's end is exclusive).
It confirms emptiness only for:

- HTTP 200 with no chart error, one matching-symbol result, empty/absent timestamps,
  and an empty quote object or empty quote arrays.
- HTTP 404 with `chart.result=null`, error code `Not Found`, and the exact
  `Data doesn't exist for startDate = <requested Unix start>, endDate = <requested Unix end>`
  description.

Generic 404/delisting messages, network errors, rate limits, server errors, malformed
responses, and unknown errors remain failures. There is no weekend/calendar guess
or catch-all empty fallback. Confirmation failures also propagate. Faulted loads
are evicted from the in-process cache so later calls can retry in the same scope;
there is no automatic retry loop.
Confirmed empty ranges (including pre-listing ranges) add coverage without fake
candles. Earlier persisted candles still return, subject to the simulated-date
cutoff. Benchmarks use the same retrieval path. Coverage suppresses repeat requests
only within the confirmed interval; the next uncovered trading date is still fetched.

To repair incorrectly certified empty coverage, stop backtests/synchronization and
back up the database. Inspect and delete **all coverage rows overlapping the affected
range** for the specific normalized `Symbol`, `Source`, and `PriceAdjustmentMode`.
For example, after reviewing matching rows, use parameterized SQL with this predicate:

```sql
DELETE FROM MarketDataCoverage
WHERE Symbol = @symbol AND Source = @source AND PriceAdjustmentMode = @mode
  AND [From] <= @to AND [To] >= @from;
```

Keep `MarketCandles` unchanged to preserve valid first-download-wins prices.
Deleting an overlapping row can reopen a larger interval than the repair range;
existing candle dates still prevent redundant candle downloads. Restart the process
to discard its successful in-memory cache, then synchronize the affected range again.
Review the fetched candles/coverage before rerunning strategy comparisons.

Progress identifies database, external-provider, or combined reads. Exact-range
in-memory caching avoids repeated database reads within a scope, while durable
coverage supports overlapping requests and subsequent processes. Every backtest
result remains filtered to its simulated date, even when the cached range is wider.
As before, failed requests are memoized within the current scope; start a new
command/scope to retry after resolving a provider or database failure.
Ordinary non-backtest market-data workflows continue using `IMarketDataProvider`.

## Ranking feature inputs

`StockRankingFeatures` in Core represents one stock on one effective analysis
date, with unweighted values rather than score contributions. The symbol,
`AnalysisDate` (`DateOnly`), momentum, average volume, volume trend, volatility,
and SMA5 comparison are required at construction. RSI, ATR, 52-week high and
distance, benchmark symbol and Relative Strength, and fundamentals are optional.
Missing optional values are `null`, never substituted with zero; individual
fundamental metrics can also be missing. Percent values retain the existing
percentage-point units; ATR and 52-week high retain price units.

The analyzer creates one row, exposes it as `ObservationResult.Features`, and
passes it through `ToScoringContext()` to the existing `IScoringEngine` boundary.
Deterministic scorers retain their existing inputs and behavior; future scorers
can read the complete row from `ScoringContext.Features`. Contexts and results
constructed by legacy callers may have no feature row.

The effective date is the supplied `analysisDate`, or the last strategy-window
candle date when omitted. Callers must continue to supply appropriately bounded,
chronological candle windows. Feature availability is independent of whether a
scoring factor is enabled; scorer configuration still controls contributions.
Fundamentals retain their own reporting date and missing-metric semantics; the
row does not perform historical snapshot selection or guarantee point-in-time
fundamental availability. Training and model invocation are not introduced.

### Ranking explanations

Console market rankings include template-based explanations of each supplied
rank, score, and factor's actual input, status, and applied contribution.
The largest positive and negative contributions are identified only when the
breakdown reconciles to the ranking score. Disabled, unavailable, neutral, and
disqualified factors remain distinct. Missing data does not establish a cause
(for example, missing RSI does not necessarily mean insufficient history).
Quality-filter reasons are copied from scorer diagnostics, not reconstructed.
ATR and fundamental snapshots are contextual information, not additional score
contributions. No explanations change scores, ranking order, or eligibility.

`IStockRankingExplanationService` separates narrative generation from scoring and
reporting, allowing a future alternative generator. The template implementation
uses `StockRankingFeatures` when present and legacy observation fields otherwise.
It retains factor evidence alongside narrative text and uses invariant,
full-precision decimal formatting. Unknown factors are reported without inventing
their inputs. Scorers without breakdowns explicitly report that causes are unknown.

Call `Explain(observation, rank, previous)` with an optional earlier observation
for the same symbol to describe score and shared contribution changes. Changed
factor sets or unavailable contributions are reported as comparison limitations.
Callers must ensure comparable strategy/run contexts; the service does not load
history or establish causality for changed inputs. Console output uses the supplied
ranking order and does not fetch previous runs or claim that a stock entered the
top five without previous rank evidence. No LLM or external service is invoked.

### Backtest and strategy comparison explanations

Console backtest summaries (including `--backtest-run-id`) and variant comparison
reports include deterministic narratives of the supplied persisted report metrics:
capital, return, trades, maximum drawdown, peak-to-trough duration, recovery, and
benchmark-relative performance. Alpha is the simple return difference, not a
risk-adjusted measure. Comparison explanations respect the selected ranking metric,
identify metric ties, and show return/drawdown differences without declaring an
overall or future winner. Without a ranking, no winner is inferred.

`IResultExplanationService` returns a `ResultExplanation` containing a summary,
details, and evidence limitations. It can be replaced by a future LLM-assisted
implementation without changing reporting or calculations. No external AI service
is called. Failed or incomplete backtests do not receive completed-outcome claims.
Unavailable benchmarks are explicitly reported. CSV formats are unchanged.

Aggregate reports do not contain holding-level return attribution or factor
breakdowns. Explanations therefore do not invent return/drawdown causes or factor
differences from strategy names or scores. Persisted stock factor contributions
explain ranking scores, not realized portfolio performance. Comparison reports
also lack dates, so the generator cannot independently verify matching periods.
Attribution and factor-history integration are follow-up work, not prediction or
strategy tuning.

### Daily feature snapshots

Apply the `AddFeatureSnapshots` migration before running analysis. Normal analysis
and each backtest day save every successfully analyzed stock, not just recommended
stocks, to `FeatureSnapshots`. Insufficient-history and failed analyses produce no
snapshot. Backtests retain their simulated date and run ID and do not fetch live
fundamentals; candle date cutoffs remain unchanged. Normal analysis discards
fundamentals with a known reporting date after the analysis date. Unknown reporting
dates remain unknown and do not establish historical publication availability.

The indexed identity is the normalized stock symbol (trimmed, uppercase), analysis
date, strategy ID, and optional backtest run ID. Normal analysis has no run ID.
Repeated saves retain the first snapshot for that identity, including its original
score/configuration; use distinct strategy IDs for distinct configurations. SQL
Server unique indexes enforce this identity, including normal-analysis rows with
a null run ID. Concurrent duplicate inserts can fail the unique constraint rather
than overwrite history.

`IFeatureSnapshotRepository.QueryAsync(from, to, symbol, strategyId, backtestRunId)`
returns detached rows in date/symbol/strategy/run order, with inclusive date bounds
and optional filters. Omitted filters include all contexts. The version-1 JSON
feature payload reuses `StockRankingFeatures` and preserves optional nulls and
decimal factor values. Rows also retain the strategy configuration JSON, optional
factor-contribution JSON, and nullable deterministic score (`decimal(28,12)`).
The schema version allows future payload evolution. Snapshot creation does not
generate future outcomes. Deleting a backtest run cascades to its feature snapshots.

### Offline supervised-training labels

Apply the `AddFutureOutcomeLabels` migration before generating labels.
`FutureOutcomeLabelWorkflow` (Infrastructure, `Training` namespace) is an explicit
offline API, registered by `AddInfrastructure`; no live analysis, scoring, or
backtest path invokes it. In a separate training job, resolve it from a service
scope and call:

```csharp
await workflow.GenerateAsync(
    from: new DateOnly(2024, 1, 1),
    to: new DateOnly(2024, 6, 30),
    availableThrough: new DateOnly(2024, 10, 1),
    benchmarkSymbol: "SPY",
    horizons: [5, 20, 60],
    backtestRunId: runId);
```

The inclusive snapshot range can also be filtered by symbol and strategy ID.
`availableThrough` is the caller's explicit last available historical date; it
must not precede `to`. The workflow requests bounded daily price history through
that date and independently excludes candles beyond it. For reproducible offline
jobs, supply a historical `IMarketDataProvider` with a fixed data vintage.

Labels use close-to-close percentage returns from an exact snapshot-date close
to the Nth subsequent observed stock trading session (not calendar days).
Defaults are 5, 20, and 60; custom positive horizons are supported and deduplicated.
History must contain complete daily sessions: gaps in supplied history cannot be
distinguished from market closures. No stale baseline is substituted. Missing
baselines, immature horizons, non-positive endpoint prices, or decimal-overflow
returns produce no stock label. Duplicate daily candles are rejected.
Benchmark returns require valid closes on the same exact start and outcome dates.
Unavailable benchmark endpoints leave the stock label's benchmark return and flag
null. `OutperformedBenchmark` is true only for a strictly greater return; false
includes ties. These are price returns, with no additional dividend/fee adjustment;
split/dividend adjustment depends on the supplied history.

Labels are separate rows keyed uniquely by snapshot ID, horizon, and normalized
benchmark symbol. Repeated generation retains the first row for each key; later
cutoffs add newly mature horizons without changing stored features, scores, or
existing labels (including missing benchmark comparisons). Concurrent duplicate
inserts can fail the unique constraint. Labels cascade-delete with their snapshot.
Use `IFeatureSnapshotRepository.QueryAsync` to select training inputs, then
`IFutureOutcomeLabelRepository.QueryAsync(snapshotId, horizonTradingDays, benchmarkSymbol)`
to load detached, ordered targets; omitted label filters include all horizons and
benchmarks. Bulk dataset jobs can join `TradingSimulatorDbContext.FeatureSnapshots`
and `FutureOutcomeLabels` on snapshot ID. Keep that join exclusively in offline
training code: labels are not part of `StockRankingFeatures`, snapshot payloads,
or scoring interfaces.

### Offline dataset export

`TrainingDatasetBuilder` (Core training namespace) is registered by
`AddInfrastructure`. Resolve it inside a service scope in a separate offline job,
after capturing feature snapshots and generating outcome labels:

```csharp
var builder = scope.ServiceProvider.GetRequiredService<TrainingDatasetBuilder>();
var dataset = await builder.BuildAsync(new TrainingDatasetQuery
{
    From = new DateOnly(2025, 1, 1),
    To = new DateOnly(2025, 12, 31),
    StrategyId = "default",
    HorizonTradingDays = 20,
    BenchmarkSymbol = "SPY",
    IncludeStrategyMetadata = true
});
await using var output = File.Create("training-dataset.json");
await TrainingDatasetJsonExporter.ExportAsync(dataset, output);
```

Import `IntelligentTradingSimulator.Core.Training`,
`IntelligentTradingSimulator.Infrastructure.Training`, and
`Microsoft.Extensions.DependencyInjection` for this example.
The builder returns an in-memory dataset; the exporter writes UTF-8 JSON to a
caller-owned stream and leaves it open. Neither component fetches prices,
generates labels, trains models, or participates in live/backtest scoring.

Schema version `1` has three top-level fields: `schemaVersion`, `query`, and
`rows`. The query records every effective filter, including normalized stock and
benchmark symbols and the strategy-metadata option. Dates are ISO `yyyy-MM-dd`;
numbers use culture-independent JSON notation; unavailable features and benchmark
targets remain explicit `null`, never zero.

Each row represents one snapshot/horizon/benchmark combination and includes:

- Source snapshot ID, feature schema version, label ID, symbol, analysis date,
  strategy ID, and optional backtest run ID.
- A `features` object containing the stored `StockRankingFeatures`, including
  optional fundamentals. Future outcomes remain outside this input object.
- Horizon in trading sessions, outcome date, stock return, benchmark symbol,
  optional benchmark return, relative return (stock minus benchmark, in percentage
  points), and the stored outperform flag.
- The captured `strategyConfigurationJson` when `IncludeStrategyMetadata` is
  true (default false). Identity fields are always included.

`From`/`To` are inclusive **analysis-date** bounds; outcomes may occur after `To`.
Omitted bounds cover all dates. Optional `Symbol`, exact `StrategyId`,
`BacktestRunId`, `HorizonTradingDays`, and `BenchmarkSymbol` filters combine with
AND. Omitted context filters include all contexts. Invalid ranges, blank filters,
and non-positive horizons/run IDs are rejected.
Only snapshots with matching stored labels produce rows; an empty result still
exports the schema and query. Multiple horizons or benchmarks repeat features
in separate rows. Ordering is deterministic by analysis date, ordinal symbol,
ordinal strategy ID, run ID, snapshot ID, horizon, then ordinal benchmark.

This initial builder materializes the dataset and queries labels per snapshot;
large datasets may need a future bulk/streaming implementation. Reproducing the
same query requires retaining the source database/data vintage: newly captured
snapshots or labels can add rows. Keep the export as the dataset artifact, and
group repeated snapshots together when splitting training/evaluation data.
Use chronological splits that account for label horizons to avoid leakage across
evaluation boundaries. There is no CLI switch for this offline service.

## Fundamental data foundation

`IFundamentalDataProvider` in Core returns a provider-neutral `FundamentalData` snapshot.
The Infrastructure `JsonFundamentalDataProvider` loads a locally persisted JSON array
from a caller-supplied file path; it does not call a live service. For example:

```json
[
  {
    "Symbol": "AAPL",
    "AsOfDate": "2025-12-31",
    "RevenueGrowthPercent": 12.5,
    "EpsGrowthPercent": null,
    "Margins": 21.75,
    "Debt": 1.5
  }
]
```

Revenue and EPS growth are year-over-year percentages (`12.5` means 12.5%).
`Margins` is net profit margin in percent; `Debt` is total debt divided by
total equity (`1.5` means 1.5:1). Future provider adapters must convert their
units to these conventions before returning the Core model.
Omitted or explicit `null` metrics and reporting dates stay unknown, never zero.
Zero and negative metrics are preserved without interpreting quality.
An unknown symbol returns `null`; invalid JSON, missing/blank symbols, duplicate
symbols (case-insensitive, ignoring surrounding whitespace), and file-access
errors propagate rather than masquerading as missing fundamentals.

The JSON file is the initial persistence boundary, maintained by the caller,
with one snapshot per symbol. Each lookup reloads it; there is no in-memory
cache or database migration. Consumers can construct the provider with a local
path and depend only on `IFundamentalDataProvider`; future live adapters or
storage implementations can replace it without changing ranking logic.
This provider is not wired into application workflows and does not apply the
Quality Filter or change existing scores. Historical/as-of snapshot selection,
refresh policies and historical integration remain follow-up work;
these snapshots must not be used as point-in-time backtest data.

### External provider: FinancialModelingPrep

`FinancialModelingPrepFundamentalDataProvider` implements `IFundamentalDataProvider` to fetch fundamental metrics directly from the current FinancialModelingPrep (FMP) stable API endpoints:

- `/stable/financial-growth?symbol={SYMBOL}&limit=1&apikey={API_KEY}`
- `/stable/ratios?symbol={SYMBOL}&limit=1&apikey={API_KEY}`

Both requests run in parallel, using the normalized uppercase symbol and URL-encoded symbol and API key. The configured `BaseUrl` and request timeout remain supported.

Configure the FMP credentials and settings in `appsettings.json`:

```json
{
  "FundamentalData": {
    "FinancialModelingPrep": {
      "ApiKey": "YOUR_API_KEY",
      "BaseUrl": "https://financialmodelingprep.com/",
      "RequestTimeoutSeconds": 10
    }
  }
}
```

`RequestTimeoutSeconds` defaults to 10 and must be between 1 and 30 seconds. When the growth and ratios endpoints report different period-end dates, only metrics from the newer period are returned; if both dates are missing, no snapshot is returned.

Never commit real API keys to source control. When configured with an API key, `FinancialModelingPrepFundamentalDataProvider` is registered in DI as the `IFundamentalDataProvider` implementation. If the API key is unconfigured or empty, fundamental data fetching is skipped during ranking workflows. In the market data ranking workflow, provider failures or missing data for individual stocks are handled safely without breaking the full ranking flow.

For manual verification with a valid FMP account, set `$env:FMP_API_KEY` securely in your local environment, then run:

```powershell
$encodedApiKey = [Uri]::EscapeDataString($env:FMP_API_KEY.Trim())
Invoke-RestMethod "https://financialmodelingprep.com/stable/ratios?symbol=MSFT&limit=1&apikey=$encodedApiKey"
Invoke-RestMethod "https://financialmodelingprep.com/stable/financial-growth?symbol=MSFT&limit=1&apikey=$encodedApiKey"
```

Configure the application API key separately in your local `appsettings.json` as shown above (never commit the key), then run the normal ranking workflow. Supported symbols should show populated `Fundamentals: AsOf=... | RevGrowth=... | EpsGrowth=... | Margins=... | Debt=...` values rather than provider-failure warnings. Values and reporting dates depend on account coverage and the current FMP response. Growth and net profit margin fractions are converted to percentage points (for example, `0.125` becomes `12.5`); debt remains a debt-to-equity ratio. If endpoint reporting dates differ, only metrics from the newer period are used.

Provider failures print an English console warning with the affected symbol and exception type, then ranking continues without fundamentals for that stock. Exception messages are omitted because they may contain credential-bearing request URLs.

## Configure a strategy

`ActiveStrategy` selects a named entry under `Strategies`. The checked-in configuration provides `default`, `conservative`, and `aggressive` entries. Change `ActiveStrategy` to one of those IDs or add a validated strategy entry:

```json
"ActiveStrategy": "default"
```

Each strategy supports these settings:

| Setting | Purpose |
| --- | --- |
| `MaxPositions` | Maximum number of recommended positions; must be greater than zero. |
| `MaxPositionWeight` | Maximum position weight, from greater than zero through 100. |
| `StopLossPercent` | Optional percentage loss from average entry price; greater than zero through 100. Omitted or `null` disables Stop Loss. |
| `TrailingStopPercent` | Optional percentage drawdown from the highest observed execution price since entry; greater than zero through 100. Omitted or `null` disables Trailing Stop. |
| `LookbackDays` | Historical lookback used by analysis; must be greater than zero. |
| `MomentumScoreWeight` | Non-negative score points per percentage point of momentum (default `10`); `0` disables the contribution. |
| `VolumeTrendScoreWeight` | Non-negative score points per percentage point of volume trend (default `0.05`); `0` disables the contribution. |
| `VolatilityScoreWeight` | Non-negative penalty points per percentage point of volatility (default `1`); `0` disables the contribution, not the quality filter. |
| `SmaScoreWeight` | Non-negative SMA5 eligibility bonus (default `5`); `0` disables the bonus, not the eligibility filter. |
| `UseRsi` | Enable RSI scoring. |
| `RsiScoreWeight` | Non-negative multiplier for both the RSI bonus and penalty (default `1`); `0` disables the score contribution. |
| `RsiPreferredMinimum`, `RsiPreferredMaximum` | Preferred RSI range, each from 0 to 100; minimum cannot exceed maximum. |
| `RsiOverboughtThreshold` | RSI penalty threshold, from 0 to 100 and not below the preferred maximum. |
| `RsiPreferredRangeBonus`, `RsiOverboughtPenalty` | Non-negative score bonus and penalty. |
| `UseRelativeStrength` | Enable relative-strength analysis and scoring (default `true`). |
| `RelativeStrengthBenchmarkSymbol` | Supported benchmark ticker for Relative Strength: `SPY` (default), `QQQ`, or `IWM`. Surrounding whitespace and letter case are normalized; blank or unsupported tickers are rejected even when the factor is disabled. |
| `RelativeStrengthScoreWeight` | Non-negative score points per percentage point of Relative Strength (default `1`). Set to `0` to retain analysis without affecting scores. |
| `Use52WeekHigh` | Enable distance-from-52-week-high scoring. |
| `Week52HighScoreWeight` | Non-negative maximum score contribution for the 52-week-high factor. |
| `UseQualityFilter` | Enable quality screening, including the existing volatility gate (default `false`). |
| `QualityMinimumRevenueGrowthPercent`, `QualityMinimumEpsGrowthPercent`, `QualityMinimumMarginsPercent` | Inclusive minimum percentages; omitted/null disables that metric's check. Negative minima are allowed. |
| `QualityMaximumDebtToEquity` | Inclusive maximum debt/equity ratio, non-negative; omitted/null disables the check. |
| `QualityFailureAction` | `Exclude` (default), `Penalize`, or `Neutral` for failed thresholds. |
| `QualityMissingDataAction` | `Neutral` (default), `Penalize`, or `Exclude` for unavailable configured metrics. |
| `QualityPenalty` | Non-negative score points deducted once for `Penalize` (default `5`; `0` is neutral). |

The loader validates strategy settings at startup. Missing settings use the defaults in `StrategyConfiguration`; invalid or missing active strategy sections stop startup with a configuration error. All weights must be non-negative decimals, including weights for disabled factors. At least one scoring factor must have a positive weight and be enabled (where a toggle exists); RSI also needs a positive bonus or penalty. Eligibility and quality filters alone are not an active scoring-factor set.

Set `"StopLossPercent": 10` on a strategy to exit an entire position when its available end-of-day close is at or below 90% of its average entry price. Both backtests and current portfolio simulations execute these sells before rebalancing and persist `ExitReason.StopLoss` in trade history. A stopped symbol is not repurchased in the same step, but may be recommended again on later steps. Missing or non-positive prices, non-positive entry prices, and non-positive quantities do not trigger a stop. Backtests use only prices available on or before the simulated date, including held symbols outside the requested universe, and do not require enough candles for scoring to evaluate a stop. No intraday fills or execution at the threshold price are modeled.

Set `"TrailingStopPercent": 10` to sell an entire position when the available close is at or below 90% of its `HighestPriceSinceEntry`, even if it remains above entry price. The high starts at the purchase price, increases with observed prices, and is persisted between current portfolio simulations. Additional buys and partial sells retain the high; a new position after full liquidation starts a new high. Tracking continues when the stop is disabled, including in current simulations with fewer than five scoring candles: available latest closes are retained regardless of either exit setting. Both modes persist `ExitReason.TrailingStop`, skip missing/non-positive prices and non-positive quantities, and prevent same-step repurchases. Stop Loss takes precedence when both stops trigger. Like Stop Loss, evaluation uses available daily closes, not intraday highs or threshold fills. Current simulation requires an available price for the held symbol; backtests also fetch held symbols outside the requested universe without requiring scoring history. The migration initializes existing positions from the greater of their known average entry/current price (or zero if neither is positive); historical highs before this migration cannot be reconstructed.

Each entry under `Strategies` binds independently, so comparison variants can use different factor toggles, weights, and Relative Strength benchmarks. For example, add these entries to compare an RSI/Relative Strength setup against a 52-week-high setup:

```json
"Strategies": {
  "rsi-relative": {
    "UseRsi": true,
    "RsiScoreWeight": 2,
    "UseRelativeStrength": true,
    "RelativeStrengthScoreWeight": 3,
    "RelativeStrengthBenchmarkSymbol": "QQQ",
    "Use52WeekHigh": false
  },
  "high-proximity": {
    "UseRsi": false,
    "UseRelativeStrength": false,
    "Use52WeekHigh": true,
    "Week52HighScoreWeight": 8
  }
}
```

Omitted base weights keep their defaults; set individual weights to `0` to disable their score contributions. `ActiveStrategy` selects one entry for normal analysis; comparison runs load and validate all entries.

### Composite scoring

Ranking and backtesting select the same Core `IScoringEngine` through each strategy's optional `ScoringEngine` setting: `Composite` (default), `RuleBased`, or `AI`. For example, set `"ScoringEngine": "AI"` inside `Strategies:default` in `appsettings.json`. Names are case-insensitive and surrounding whitespace is ignored. Numeric values, combined names, empty strings, and unknown names fail configuration loading with the configuration key and supported choices. `ActiveStrategy` selects the strategy for normal ranking and backtesting; strategy comparisons use each variant's own engine setting.

`AI` selects the deliberately non-production `AiScoringEngine` placeholder. It delegates to composite scoring with the strategy's configured weights, filters, and diagnostics; it does not load a model, call a network service, or train anything. Its replaceable `scoringAdapter` constructor parameter accepts an `IScoringEngine` adapter for future inference. The input contract is `ScoringContext`, including the complete `StockRankingFeatures` row in `Features` when supplied by analysis (legacy callers may omit it). Optional feature values remain null when unavailable. The output contract is a decimal ranking score, with higher values ranked first and `StrategyConfiguration.DisqualifiedScore` indicating ineligibility, plus optional applied Relative Strength and factor diagnostics. Adapters without diagnostics leave them null; adapter failures propagate rather than silently falling back. Real inference and its model-specific validation remain future work.

The default `CompositeScoringEngine` accepts the already-calculated metrics in `ScoringContext` and adds contributions in a fixed order using decimal arithmetic:

1. Momentum percentage points × `MomentumScoreWeight`.
2. Volume-trend percentage points × `VolumeTrendScoreWeight`.
3. Negative volatility percentage points × `VolatilityScoreWeight` (lower volatility is better).
4. SMA5 eligibility bonus of `SmaScoreWeight` (default +5).
5. RSI: `RsiPreferredRangeBonus × RsiScoreWeight` inside the inclusive preferred range, or negative `RsiOverboughtPenalty × RsiScoreWeight` at/above the overbought threshold (penalty takes precedence).
6. 52-week-high distance: `(clamp(distance, -100, 0) + 50) / 50 × Week52HighScoreWeight`; nearer the high is better, -50% is neutral, and the normalized multiplier is bounded to [-1, 1].
7. Relative Strength percentage points × `RelativeStrengthScoreWeight`.
8. Quality Filter: zero or negative `QualityPenalty`, according to the configured fundamental policy.

These contributions use metrics calculated by the analyzer. Most use the strategy's analysis window; the 52-week-high factor uses a separate 252-session history:

| Factor input | Calculation |
| --- | --- |
| Momentum | `(last close - first close) / first close × 100`, in percent. |
| Volume trend | `(last volume - first volume) / first volume × 100`, in percent. Average volume does not affect this score. |
| Volatility | Average of `(candle high - candle low) / candle close × 100` across the analysis candles. The score subtracts this value multiplied by `VolatilityScoreWeight`. |
| SMA5 eligibility | The last close must be strictly greater than the average of the last five available closes. Eligible observations receive `SmaScoreWeight` points. |
| RSI | 14-period RSI using Wilder smoothing over chronological closing prices; it is unavailable until at least 15 closes are present. A no-change series returns 50. The configured thresholds and points determine its contribution as described above. |
| 52-week-high distance | Highest high among the most recent 252 trading sessions, compared with the latest close: `(latest close - high) / high × 100`. The resulting percentage is clamped only when scoring. |
| Relative Strength | Stock close-to-close return minus the selected benchmark's return, in percentage points; both benchmark endpoint closes must match the stock's first and last analysis dates. |

SMA5 is an eligibility gate even when `SmaScoreWeight` is zero. When `UseQualityFilter` is enabled, average volatility above 3% also disqualifies the observation; either disqualification produces the sentinel score `-999` instead of the sum of factor points.

### Fundamental quality screening

Fundamental rules are opt-in: configure thresholds on a strategy and set `UseQualityFilter`
to `true`. No implicit fundamental thresholds are imposed. For example:

```json
{
  "UseQualityFilter": true,
  "QualityMinimumRevenueGrowthPercent": 5,
  "QualityMinimumEpsGrowthPercent": 0,
  "QualityMinimumMarginsPercent": 10,
  "QualityMaximumDebtToEquity": 2,
  "QualityFailureAction": "Exclude",
  "QualityMissingDataAction": "Penalize",
  "QualityPenalty": 7
}
```

Any failed configured threshold triggers the failure policy; equality passes.
Only configured metrics require data. An absent snapshot or null configured field
triggers the missing-data policy, not a zero substitution. Negative debt/equity is
treated as unusable data (it must not pass a maximum-debt rule simply because it
is negative); negative growth or margins are valid and compared normally.
When failures and missing fields coexist, `Exclude` takes precedence over
`Penalize`, then `Neutral`. Penalties apply once per stock, not once per metric.
Excluded stocks receive `-999` and cannot be recommended, but remain in analysis
output for inspection. Passing stocks get no quality bonus.

The enabled filter adds a `QualityFilter` breakdown entry with applied points and
an explanation listing failed values/thresholds and missing metrics with their
selected actions. Disqualification clears applied points and retains the existing
`Disqualification=-999` override. Disabled quality screening adds no entry and
does not change scores. Existing SMA5 and volatility gates remain unchanged,
regardless of the fundamental failure/missing policies.

Normal ranking uses the existing fundamental provider integration; provider failures
for a stock are handled as missing data without aborting the universe. Configure
FMP as described above to supply live snapshots. Backtests use the same policy but
have no historical fundamental snapshots, so configured checks use the missing-data
action. Today's snapshots are deliberately not fetched for historical dates to avoid
look-ahead bias. Point-in-time fundamentals remain follow-up work.

Final stock rankings sort by composite `Score` descending, then by symbol ascending using ordinal (case-sensitive, culture-independent) comparison for equal scores. Market analysis, persisted rank positions, recommendations, and backtests use this same ordering. Recommendation eligibility filters remain unchanged and the position limit is applied after sorting; raw factor values and contribution breakdowns remain available in ranked observations.

Weights scale contributions into score points; they need not sum to one. Percentage inputs are not divided by 100 or normalized across the stock universe, so adding another stock cannot change an existing stock's score. Default weights preserve the previous formula. Optional factors contribute zero when disabled or missing; base metrics are required decimal inputs (NaN/infinity are not representable). Arithmetic overflow is surfaced, not replaced by a silent fallback. Stocks not above SMA5, or rejected by the enabled quality filter, still receive `DisqualifiedScore` (-999).

`CompositeScoringEngine.CalculateContributions` returns a `CompositeScoreResult` with each applied contribution, eligibility, and the final score. The scoring interface also supplies a typed `FactorScoreContribution` breakdown through `ObservationResult.FactorContributions`, available in normal analysis and backtest observations. Market ranking output prints all seven technical factors and the enabled Quality Filter, including the configurable SMA5 bonus, in score points with full decimal precision. Contributions sum to the score used for ranking; disqualified stocks have zero applied factor points plus an explicit `Disqualification` contribution of -999 explaining the sentinel score.

Each factor reports `Applied`, `Disabled`, `Unavailable`, or `Disqualified`. Missing input is reported as `Unavailable` with a null contribution (`N/A`), even if the factor is disabled; it does not add score points. Available disabled factors (including zero weights) report zero, while an available enabled factor with a neutral effect reports `Applied` with zero. Custom scoring engines without breakdown support report `N/A` rather than fabricated contributions. The existing Relative Strength diagnostic and persisted observation-history format remain unchanged. `RuleBasedScoringEngine` delegates to the composite engine for compatibility. ATR remains informational; no ATR scoring, stops, or allocation rules are introduced.

Relative Strength is the stock's close-to-close return minus the selected benchmark's return, in percentage points (not a price ratio or RSI). Positive values indicate outperformance; negative values indicate underperformance. Analysis uses the stock's first and last available sessions in the requested lookback, with benchmark closes required on those exact dates. No candles after the analysis day are used, including during backtests. Missing or ambiguous endpoints, fewer than two distinct sessions, non-positive endpoint prices, or arithmetic overflow produce `null` (`N/A` in market ranking details); benchmark download failures do not prevent stock analysis. Interior missing sessions do not affect close-to-close returns. `UseRelativeStrength=false` skips benchmark fetching and leaves the value unavailable.

The value is exposed as `ObservationResult.RelativeStrengthPercent` alongside `RelativeStrengthBenchmarkSymbol`, including in `BacktestDayResult.Observations`. Eligible stocks receive `RelativeStrengthPercent * RelativeStrengthScoreWeight` score points: for example, +10pp with weight 2 adds 20 points, while -10pp subtracts 20. Missing Relative Strength, a disabled factor, or a zero weight contributes zero; SMA5 and quality-filter disqualifications remain unchanged. This factor is enabled by default, so scores with available benchmark data now include it. Decimal scoring overflow is surfaced as an error rather than silently substituting a contribution.

`ObservationResult.RelativeStrengthScoreContribution` and the market ranking's `RSScore` field report the contribution actually applied to the final score. Custom scoring engines that do not provide factor diagnostics report `N/A`. Like RSI and 52-week-high details, these diagnostic fields are not saved in the existing observation-history table; the final score is saved as before.

Market ranking details also show `ATR` (`ObservationResult.Atr`), in absolute price units rather than percent. ATR uses a fixed 14-session period in stock analysis and requires at least 15 chronological candles from the strategy's analysis lookback (`LookbackDays`), not the extended 52-week-high history. Each true range is the maximum of high minus low, absolute high minus previous close, and absolute low minus previous close. The first 14 true ranges are averaged, then subsequent ranges use Wilder smoothing. Insufficient history, invalid high/low/close prices, or arithmetic overflow yield `null` (`N/A` in the output); a flat price series yields zero. ATR is informational only: it does not affect scoring, stops, or allocation and is not persisted in observation history.

## Strategy experimentation flow

Complete the database/application setup above first. All commands below run from the repository root. Edit the source `IntelligentTradingSimulator.App/appsettings.json`, not its copy under `bin`; `dotnet run` builds and copies the updated configuration. The app reads this JSON file from its output directory, not command-line or environment-variable configuration overrides.

### 1. Define and select a variant

The keys under `Strategies` are the strategy IDs. Keep `ConnectionStrings` unchanged and replace only `ActiveStrategy` and `Strategies` with the following properties in your existing JSON file. This example retains `default` as a baseline and adds `rsi-preferred`, changing only RSI enablement so the comparison isolates that factor:

```json
"ActiveStrategy": "rsi-preferred",
"Strategies": {
  "default": {
    "MaxPositions": 10,
    "MaxPositionWeight": 100,
    "LookbackDays": 90,
    "UseRsi": false,
    "RsiPreferredMinimum": 50,
    "RsiPreferredMaximum": 70,
    "RsiOverboughtThreshold": 80,
    "RsiPreferredRangeBonus": 5,
    "RsiOverboughtPenalty": 5,
    "UseRelativeStrength": true,
    "Use52WeekHigh": false,
    "Week52HighScoreWeight": 5,
    "UseQualityFilter": false
  },
  "rsi-preferred": {
    "MaxPositions": 10,
    "MaxPositionWeight": 100,
    "LookbackDays": 90,
    "UseRsi": true,
    "RsiPreferredMinimum": 50,
    "RsiPreferredMaximum": 70,
    "RsiOverboughtThreshold": 80,
    "RsiPreferredRangeBonus": 5,
    "RsiOverboughtPenalty": 5,
    "UseRelativeStrength": true,
    "Use52WeekHigh": false,
    "Week52HighScoreWeight": 5,
    "UseQualityFilter": false
  }
}
```

This is a property fragment, not a replacement for the whole file: keep the enclosing braces and the comma after `ConnectionStrings`. To retain the checked-in `conservative` and `aggressive` variants instead, add `rsi-preferred` inside the existing `Strategies` object and change `ActiveStrategy`.

The settings and validation ranges are listed in [Configure a strategy](#configure-a-strategy). Omitted settings use model defaults, not values inherited from `default`. A strategy's ID comes from its section name; no `Id` property is needed. Single backtests and the regular simulation select `ActiveStrategy`; there is no CLI strategy-selection flag.

### 2. Run one backtest

With `ActiveStrategy` set to `rsi-preferred`, run:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-backtest \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --backtest-name=rsi-preferred-Q1-2025
```

The startup output should identify the selected variant and period:

```text
Starting backtest: rsi-preferred-Q1-2025
Strategy:  rsi-preferred
Period:    2025-01-01 to 2025-03-31
```

After a successful run, `===== BACKTEST SUMMARY =====` includes `Run: rsi-preferred-Q1-2025 (Id=...)`, `Strategy: rsi-preferred`, capital, profit, return, trades, rebalances, risk, and available benchmark comparisons. Save the actual numeric run ID to reload its summary using `--backtest-run-id` as shown below. An engine-reported failure prints `Backtest failed:` and its error instead; infrastructure errors such as an unavailable database can terminate the single run with an exception.

### 3. Run and compare multiple variants

Use the same explicit dates and a selected sort metric:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-variants \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --comparison-sort-by=ReturnPercent
```

With the two-entry configuration above, expect `Running 2 strategy variant(s)...`, a summary for each variant, then `===== STRATEGY COMPARISON REPORT =====`. The checked-in configuration instead runs three variants: `default`, `conservative`, and `aggressive`.

`--run-variants` runs every entry under `Strategies`, not just `ActiveStrategy`; there is no CLI subset filter. Keep only the desired entries in your local configuration to limit an experiment, and keep `ActiveStrategy` valid because it is loaded at startup even for a batch. At most 100 definitions are allowed for a batch. Do not combine `--run-backtest` with `--run-variants`: the single-backtest branch takes precedence. `--backtest-name` names a single run only; batch names are generated from the period and strategy ID.

The runner executes variants sequentially and persists separate runs. They share the date range, stock universe, and 100,000 initial capital, while each uses its own strategy settings. Batch history starts far enough back for the longest configured `LookbackDays`. Individual failures remain visible in the comparison rather than becoming successful results.

To rank against the same benchmark, run:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-variants \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --comparison-sort-by=AlphaPercent \
  --comparison-benchmark=SPY
```

This starts new backtests; it does not re-sort a saved batch. The benchmark flag selects existing comparison metrics for ranking, not additional benchmark downloads. The application currently reports `SPY`, `QQQ`, and `IWM`; choose one of these for benchmark-relative ranking.

### 4. Interpret and record the comparison

An illustrative excerpt from an alpha-sorted report follows. Values, run IDs, winner, and numeric formatting depend on the actual data and local culture; these are not expected investment returns. The other variant's entry is omitted here for brevity:

```text
===== STRATEGY COMPARISON REPORT =====
Sorted by: AlphaPercent (highest first)
Benchmark: SPY
Top-ranked strategy: rsi-preferred

Strategy:       rsi-preferred
  Rank:         1
  Status:       Completed
  Start Capital:  100,000.00
  Final Capital:  108,000.00
  Return:         8.00%
  Max Drawdown:   6.00%
  DD Duration:    10 day(s)
  Recovery Time:  15 day(s)
  Trades:         12
  --- Benchmark ---
  SPY
    Benchmark Return: 5.00%
    Relative Return:  3.00%
    Alpha:            3.00%
    Outcome:          Outperform
```

- **Return** is the strategy's total percentage return, not an annualized return. Compare it alongside **Max Drawdown**, the largest peak-to-trough percentage loss; a higher return can come with worse risk.
- **DD Duration** is the calendar-day interval from the peak to the trough of the maximum drawdown. **Recovery Time** is the interval from that trough back to the previous peak; `N/A` distinguishes no drawdown from an unrecovered drawdown in the comparison.
- **Trades** counts activity, not profitability. Ranking by it does not identify the best risk-adjusted strategy.
- **Benchmark Return** uses the same run period. **Relative Return** is strategy return minus benchmark return in percentage points. **Alpha** currently equals that same difference; it is not a beta-adjusted or risk-adjusted calculation. **Outcome** is `Outperform`, `Underperform`, or `Match`.
- **Rank** and **Top-ranked strategy** refer only to the chosen metric. See the sorting rules below for direction, tie-breaking, and unranked failures or missing benchmark data. Without a sort flag, no ranks or winner are printed.

For repeatable experiments, save the strategy JSON (without credentials), explicit dates, source revision, run IDs, and console output. Change one factor at a time and compare return, risk, and the same benchmark rather than selecting by return alone. Market data is retrieved from Yahoo Finance, so provider availability or revised history can affect reruns. Historical simulation performance does not guarantee real-world investment outcomes.

## Run the application

From the repository root, run the standard simulation:

```bash
dotnet run --project IntelligentTradingSimulator.App
```

This uses the active strategy, requests current historical market data for the configured stock universe, prints rankings and portfolio reports, and accesses the SQL Server database.

Run a backtest for an explicit date range:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-backtest \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --backtest-name=Example
```

If dates are omitted, the end date defaults to yesterday and the start date defaults to one year before the end date. The backtest uses the active strategy and an initial capital of 100,000. Its summary is printed after the run completes.

Run all configured strategy variants over the same date range:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-variants \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31
```

The variants command loads every entry under `Strategies` and prints a comparison of completed results.
Each variant uses its own `LookbackDays`: its historical start is the backtest
start date (at midnight) minus that many calendar days, just as for a standalone
backtest with the same strategy and dates. The console comparison identifies each
variant's backtest period, historical data window (historical start through backtest
end), and initial lookback in days, including failed runs. This is the requested
strategy window; indicators such as the 52-week high may fetch additional history.
The historical start remains fixed as the simulation advances; it is not a rolling
lookback. Equivalent input windows do not guarantee identical results if external
market or fundamental data changes between executions.

Add `--comparison-sort-by=ReturnPercent` to rank the comparison by total return.
Supported metrics are `ReturnPercent`, `MaxDrawdownPercent`, `TradesCount`,
`AlphaPercent`, and `RelativeReturnPercent` (case-insensitive).
Use a metric name, not a numeric enum value; empty or unknown names are rejected.
Max drawdown sorts lowest first; all other metrics sort highest first.
Trade-count ranking measures activity, not profitability.
For alpha or relative return, also specify `--comparison-benchmark=SPY` (or another
configured benchmark symbol). Only that benchmark's existing metrics are used.
The benchmark symbol is trimmed; omitting it or providing only whitespace is
rejected when sorting by alpha or relative return.

The report shows the selected metric, each eligible strategy's sequential rank,
and the top-ranked strategy. Equal values are ordered by strategy ID using ordinal
comparison; identical IDs and values retain input order. Failed runs and results
without the selected benchmark remain visible at the end, ordered by strategy ID
and unranked (`N/A`). If no result is eligible, the top strategy is `N/A`.
Without `--comparison-sort-by`, the original input order and output are preserved.
Sorting does not change any metric calculations.

Print the summary for a previously persisted backtest by its run ID:

```bash
dotnet run --project IntelligentTradingSimulator.App -- --backtest-run-id=123
```

Replace `123` with the database ID reported for a completed run.

## Benchmarks and output

Successful backtest summaries include benchmark comparisons for `SPY`, `QQQ`, and `IWM`, using the same start and end dates as the run. Benchmark data is fetched from Yahoo Finance when the summary is generated; unavailable benchmark data may result in no benchmark section.

### Strategy comparison CSV export

Add `--export-comparison-csv=<path>` to `--run-variants` to export the same
comparison report printed to the console:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-variants \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --comparison-sort-by=ReturnPercent \
  --export-comparison-csv=comparison.csv
```

The export preserves report order and ranks, with one CSV record per strategy
variant, including failures. Strategy names are the report's `StrategyId` values.
Base columns match the comparison entry fields:

```text
StrategyId,Rank,IsSuccess,ErrorMessage,StartDate,EndDate,HistoricalStartDate,StartCapital,FinalCapital,ReturnPercent,MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount
```

For each benchmark present anywhere in the report, columns are appended in
ordinal symbol order as `<symbol>.StrategyReturnPercent`,
`<symbol>.BenchmarkReturnPercent`, `<symbol>.RelativeReturnPercent`,
`<symbol>.AlphaPercent`, and `<symbol>.Outcome`. For example, a report with
`QQQ` and `SPY` has both column groups on every row; benchmarks do not create
extra rows. Unavailable benchmark metrics, ranks, and recovery times are empty.
Failed variants retain their status and error but have empty performance metrics.
Dates use `yyyy-MM-dd`. `StartDate` and `EndDate` identify the backtest period;
`HistoricalStartDate` identifies each variant's requested history start. Historical
start is empty when unavailable (for example, a summary loaded from an older
persisted run); it is not reconstructed from potentially changed configuration.
No calculations or rankings are changed by export.

Files use UTF-8 without a BOM, CRLF record separators, and invariant decimal
numbers (percentage points, without `%` signs). Commas, quotes, and line breaks
in text are CSV-escaped; formula-like text is prefixed with an apostrophe for
spreadsheet safety. Negative numeric metrics remain numbers. Empty reports
produce only the base header. Existing files are overwritten; the parent
directory must already exist. File-system errors propagate as with other CSV
exports.

The option requires `--run-variants` and cannot be combined with
`--run-backtest` or `--backtest-run-id`, which select other flows. Missing or
duplicate export paths are rejected before executing backtests. Single-run
summary and snapshot exports remain separate options.

### CSV summary export

Add `--export-backtest-csv=<path>` to a single backtest command to export its
successful summary while retaining console output:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-backtest \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --backtest-name=Example \
  --export-backtest-csv=backtest-summary.csv
```

A persisted run can also be exported:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --backtest-run-id=123 \
  --export-backtest-csv=backtest-summary.csv
```

The parent directory must exist. The file is overwritten, using UTF-8 without a
BOM, commas, CRLF record endings, invariant decimal numbers without rounding or
percent signs, and ISO dates (`yyyy-MM-dd`). Percent values are percentage points
(for example, `10.5` means 10.5%). Failed runs or unsuccessful summaries are not
exported, including failed or still-running persisted runs with partial snapshots.
File-system errors propagate; the console summary is printed first.
The flag currently supports single-run reporting, not `--run-variants`.

Columns always appear in this order:

```text
BacktestRunId,BacktestName,StrategyId,StartDate,EndDate,InitialCapital,FinalCapital,Profit,ReturnPercent,MaxDrawdownPercent,DrawdownDurationDays,RecoveryTimeDays,TradesCount,RebalancesCount,BenchmarkSymbol,BenchmarkReturnPercent,RelativeReturnPercent,AlphaPercent,BenchmarkOutcome
```

There is one row per benchmark comparison, ordered by symbol (ordinal), repeating
the run's summary fields.
A run without benchmarks has one row with empty benchmark columns. Empty
`RecoveryTimeDays` means no drawdown or no recovery, as in the existing summary.
Alpha and relative return retain the existing strategy-minus-benchmark definition.
The writer accepts multiple summaries in input order for future batch export;
use `BacktestRunId` to distinguish runs, and do not sum repeated summary values.

Text containing commas, quotes, or line breaks is CSV-escaped. Potential
spreadsheet formulas in text fields are prefixed with an apostrophe for safety;
negative numeric metrics are unchanged.

Console output can still be captured as text from a Bash-compatible shell:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-backtest \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --backtest-name=Example | tee backtest-output.txt
```

This captures human-readable console output, not a machine-readable export.

### Daily snapshot CSV export

Export persisted daily snapshots for a selected run without generating a console
summary or fetching benchmark data:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --backtest-run-id=123 \
  --export-backtest-snapshots-csv=backtest-snapshots.csv
```

Alternatively, add `--export-backtest-snapshots-csv=<path>` to `--run-backtest`
to export after the backtest succeeds. In both modes, a snapshot-only request
skips summary generation and summary benchmark fetching; request
`--export-backtest-csv=<path>` as well to generate the summary. This does not skip
market data needed to execute a new backtest. The option does not support batch variant
export. A selected run takes precedence over `--run-backtest`, as with summary
reporting. Summary and snapshot exports can be requested together using separate
output paths. Paths are compared as absolute paths with `.` and `..` resolved
(case-insensitively on Windows). Colliding paths are rejected before running a
backtest or writing either export, leaving any existing output untouched.

The schema is:

```text
Date,PortfolioValue,Cash,ProfitLoss,ReturnPercent,TradesExecuted,RebalanceExecuted
```

Each persisted snapshot produces exactly one row, ordered by date; missing days
are not filled in and records are not aggregated or deduplicated. Values are
exported as persisted, without recalculation. Dates use `yyyy-MM-dd`, decimals use
invariant full-precision numbers without grouping or percent signs, and booleans
are `true`/`false`. `ReturnPercent` is in percentage points (`10.5` means 10.5%).
The file uses UTF-8 without a BOM, commas, and CRLF line endings.

The parent directory must exist and existing files are overwritten. An existing
run with no snapshots produces a header-only file; an unknown run prints an error
without touching the file. Selected failed or running runs can export their
persisted partial snapshots for investigation. File-system errors propagate.

## Trade exit reasons

Sell decisions can carry an `ExitReason`: `Manual`, `Rebalance`, `StopLoss`, or
`TrailingStop`. The trading engine records the supplied reason on the trade;
unlabelled new sells default to `Manual`, and portfolio rebalancing sells are
tagged `Rebalance`. Buy trades have no exit reason.

Apply the `AddTradeExitReason` migration before using the updated application.
Historical trades retain a null reason because their original cause is unknown.
Reasons are available through loaded portfolio trades and can be filtered via
`TradingSimulatorDbContext.Trades` for reporting and analytics.

New completed backtests persist their portfolio and trade history. Apply the
`AddBacktestPortfolio` migration, then load a run with
`BacktestRunRepository.GetByIdAsync` and query its `Portfolio.Trades`.
Normal portfolio queries exclude backtest portfolios. Older runs have no saved
portfolio; their trade history cannot be reconstructed.

This records reasons supplied by callers; the current application does not yet
implement stop-loss or trailing-stop triggers. No new exit rules are introduced.

## Offline model evaluation

Use `TrainingDatasetBuilder.BuildAsync` to load stored historical features and
labels, then `OfflineModelEvaluationService.Evaluate` to score them in memory.
This is a library workflow, not a new CLI mode; neither evaluation nor report
export fetches market data, trains a model, or makes deployment decisions.

```csharp
var dataset = await datasetBuilder.BuildAsync(new TrainingDatasetQuery
{
    From = new DateOnly(2025, 1, 1),
    To = new DateOnly(2025, 3, 31),
    StrategyId = "default",
    HorizonTradingDays = 5,
    BenchmarkSymbol = "SPY"
});
var baseline = new StrategyConfiguration();
var evaluator = new OfflineModelEvaluationService(
    new AiScoringEngine(baseline), baseline);
var report = evaluator.Evaluate(dataset, new OfflineEvaluationRequest
{
    ModelId = "composite-placeholder-v1",
    TrainingCutoff = new DateOnly(2024, 12, 31),
    OutcomeCutoff = new DateOnly(2025, 4, 10),
    TopCount = 10
});
await using var output = File.Create("offline-evaluation.json");
await OfflineEvaluationJsonExporter.ExportAsync(report, output);
```

The types above are in `IntelligentTradingSimulator.Core.Training`,
`IntelligentTradingSimulator.Core.Models`, `IntelligentTradingSimulator.Core.Services`,
and `IntelligentTradingSimulator.Infrastructure.Training`.
Replace the AI engine's `scoringAdapter` with a fixed local model adapter to
evaluate a real model; the default placeholder deliberately matches composite
scoring. The baseline always uses `CompositeScoringEngine` with the supplied
configuration, regardless of that configuration's engine selector.

Choose one strategy, label horizon, and benchmark per query. Date bounds are
inclusive. Evaluation must start strictly after `TrainingCutoff`, and end on or
before `OutcomeCutoff`. Rows with outcomes beyond that cutoff or outside the
query are excluded and counted. Feature dates/symbols must match their rows;
future-dated fundamentals, non-forward outcomes, unsupported schemas, and
duplicate symbols within a date/run cohort are rejected before any scoring.
Only features reach either scorer, never outcome labels.

Metrics are calculated separately for model and baseline:

- Directional accuracy is the percentage of eligible rows whose score sign
  matches the forward-return sign; zero only matches zero. Ranking scores are
  not necessarily return forecasts, so interpret this as a sign diagnostic.
- Rank correlation is Spearman correlation using average ranks for ties,
  calculated per analysis date/backtest run and averaged equally across valid
  cohorts. Singleton or constant-rank cohorts have no correlation and do not
  enter the mean; the report includes the valid cohort count.
- Top forward return is the arithmetic mean of forward returns for up to
  `TopCount` eligible selections per date/run, ordered by descending score then
  ordinal symbol. Each selection has equal weight; this is not a portfolio
  backtest or compounded return.

The disqualification sentinel is excluded independently from each engine's
metrics and selections, but retained in report rows. Empty metrics are `null`,
not zero. Versioned JSON includes the query, model ID, cutoffs, baseline
configuration, metrics, excluded count, scores, and feature/label identifiers.
Repeat with different query dates while keeping the same model and configuration.

Callers must ensure the model was frozen using only information available by
`TrainingCutoff`, including training labels whose **outcome dates** do not exceed
that cutoff. Evaluation cannot inspect an adapter's training provenance.
Historical feature snapshots must themselves be point-in-time safe: a
fundamental reporting-period end alone does not establish its publication date.
Unreported publication timing and upstream feature leakage cannot be inferred
or repaired by this evaluator. No claim of real-world investment performance
is made.

### Offline strategy tuning research

`IStrategyTuningAnalysisService.Analyze(StrategyTuningRequest)` accepts multiple
historical `BacktestSummaryReportResult` values, each paired with an optional
historical `StrategyConfiguration`. Obtain summaries through
`IBacktestSummaryReportService.GenerateAsync(runId)` and supply the parameter
snapshots actually used for those runs. Backtest run records do not store those
snapshots: do not substitute today's active configuration or infer parameters
from a strategy name. Missing snapshots are reported and cannot be promising
parameter candidates. No new backtests or external AI calls are made.

The initial assistant is deterministic (`heuristic-v1`), not a trained optimizer.
It reuses comparison return ranking and benchmark/risk metrics. Versioned JSON
and a replaceable analysis interface support future optimization consumers
without granting permission to change or deploy strategies.

For database-free console analysis and optional JSON export:

```bash
dotnet run --project IntelligentTradingSimulator.App --configuration Release -- \
  --tuning-input=/tmp/tuning-request.json \
  --export-tuning-json=/tmp/tuning-research.json
```

Both options require a non-empty path and may occur only once. Export requires
input, output must be a new file (existing files and link aliases are never
overwritten), and tuning cannot be combined with backtest execution/report/export
modes. Invalid input exits with a nonzero status.
The input is a JSON `StrategyTuningRequest` (camelCase or PascalCase properties;
enum names or numbers). For example, two illustrative, not investment-recommended
variants using default parameters except for Stop Loss:

```json
{
  "schemaVersion": 1,
  "benchmarkSymbol": "SPY",
  "minimumReturnPercent": 0,
  "highRiskDrawdownPercent": 20,
  "runs": [
    {
      "configuration": { "id": "stop-5", "stopLossPercent": 5 },
      "summary": {
        "backtestRunId": 1, "strategyId": "stop-5",
        "isSuccess": true, "status": "Completed",
        "startDate": "2025-01-01", "endDate": "2025-12-31",
        "startCapital": 100000, "finalCapital": 115000, "returnPercent": 15,
        "maxDrawdownPercent": 8, "recoveryTimeDays": 20, "tradesCount": 12,
        "benchmarkComparisons": [
          { "symbol": "SPY", "strategyReturnPercent": 15,
            "benchmarkReturnPercent": 10, "relativeReturnPercent": 5, "alphaPercent": 5 }
        ]
      }
    },
    {
      "configuration": { "id": "stop-10", "stopLossPercent": 10 },
      "summary": {
        "backtestRunId": 2, "strategyId": "stop-10",
        "isSuccess": true, "status": "Completed",
        "startDate": "2025-01-01", "endDate": "2025-12-31",
        "startCapital": 100000, "finalCapital": 125000, "returnPercent": 25,
        "maxDrawdownPercent": 25, "recoveryTimeDays": null, "tradesCount": 14,
        "benchmarkComparisons": [
          { "symbol": "SPY", "strategyReturnPercent": 25,
            "benchmarkReturnPercent": 10, "relativeReturnPercent": 15, "alphaPercent": 15 }
        ]
      }
    }
  ]
}
```

For actual research, serialize complete historical configurations, not partial
objects that would fill omitted parameters with current code defaults.
At least two uniquely identified runs are required; strategy IDs may repeat.
Completed runs must share exact dates, positive initial capital, and the selected
benchmark's return when available. Configuration IDs must match strategy IDs.
Their capital values must also agree with reported return, allowing for independent
four-decimal rounding. JSON inputs must explicitly include `maxDrawdownPercent` for
completed runs; failed/incomplete runs may omit it and remain visible but unranked
and unanalyzed. Invalid metrics, duplicate run IDs/selected benchmarks, and
inconsistent selected benchmark return differences are rejected. Ties use ordinal
strategy ID, then ascending run ID.

High-performing means return **strictly exceeds** the nonnegative research
minimum and outperforms the selected benchmark. High-risk means positive maximum
drawdown **reaches or exceeds** the configured threshold (default 20%, allowed
range `(0, 100]`). A high-performing run can also be high-risk. Promising requires
high performance, below-threshold drawdown, executed trades, no unrecovered
maximum drawdown, a parameter snapshot, and at least two completed runs.
Missing benchmark evidence prevents high-performance/promising flags rather than
silently assuming zero benchmark return. These are screening rules, not an
overall winner or a forecast.

Console/JSON output includes parameter combinations, return ranks, metrics,
strengths, weaknesses, thresholds, and limitations. It never mutates parameters.
Aggregate metrics cannot prove the parameter changes caused performance or
verify matching universes, transaction costs, or historical data provenance.
Researchers must check those assumptions and repeat evaluation on held-out
periods; #57's offline scoring evaluation remains a separate complementary
workflow, not evidence automatically supplied by this analysis. Future automated
search, model-driven advice, and production deployment are not implemented.

## Verify local setup

From the repository root, restore, build, and test the solution using the SDK configured in `global.json`. The tests use in-memory databases and do not require a running SQL Server or Yahoo Finance access:

```bash
dotnet restore IntelligentTradingSimulator.sln
dotnet build IntelligentTradingSimulator.sln --configuration Release --no-restore
dotnet test IntelligentTradingSimulator.sln --configuration Release --no-build --verbosity normal
```

To run the test suite using the Debug configuration:

```bash
dotnet test IntelligentTradingSimulator.sln --configuration Debug --verbosity normal
```

To run the test suite using the Release configuration:

```bash
dotnet test IntelligentTradingSimulator.sln --configuration Release --verbosity normal
```

## Troubleshooting

- **`Connection string 'TradingDb' is not configured.`** Check that `ConnectionStrings:TradingDb` exists in `IntelligentTradingSimulator.App/appsettings.json` and that the updated file is copied to the app's output directory. The app does not read `TRADING_DB_CONNECTION`.
- **EF reports that `TRADING_DB_CONNECTION` is missing.** Export that variable in the same shell where you run `dotnet ef database update`; use the same server, database, and credentials as the app's JSON connection string.
- **SQL Server connection refused or timed out.** Confirm the SQL Server service is running, listening on the configured host and TCP port, and reachable from your machine. Check the instance name, firewall, login/password, and database permissions.
- **A table or schema is missing.** Apply migrations with the `dotnet ef database update` command above before starting the application.
- **Yahoo Finance returns no data or a request fails.** Check internet access, confirm the symbol/date range is valid, and retry later if the provider is temporarily unavailable. Application simulation and backtest commands depend on this external provider.
- **The selected strategy cannot be found or fails validation.** Ensure `ActiveStrategy` exactly names an entry under `Strategies`, and check the numeric ranges in the strategy table.
