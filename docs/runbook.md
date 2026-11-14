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

## Financial precision and reconciliation

Execution normalizes market prices to 12 decimal places and share quantities to
8 decimal places. Prices and buy quantities round to nearest with midpoint
`AwayFromZero`; partial-sale quantities round toward zero so they cannot exceed
the requested amount or holdings. A full liquidation uses the exact remaining
holding, including legacy quantities. An exact full-value request at the supplied
market price retains its liquidation intent even if execution-price normalization
rounds upward; execution and cash accounting still use the normalized price.
Average entry prices round to 12 places
with midpoint `AwayFromZero`; each weighted-average update introduces at most
`0.0000000000005` of absolute rounding error, and prior error is weighted by the
prior quantity rather than amplified.

Cash and each executed trade's notional use 8 decimal places, rounding to nearest
with midpoint `AwayFromZero`. Cash is debited or credited by that rounded
`quantity × normalized price`, never by the requested allocation. Reconciliation
must apply the same per-trade rounding to signed notionals. Each cash movement
differs from the unrounded product by at most `0.000000005`; after N trades, the
absolute cumulative rounding difference is bounded by `N × 0.000000005`.
Buy quantities are sized from the original requested allocation, without first
rounding it to cash precision. Executed notional may exceed that original allocation by at most
`price × 0.000000005 + 0.000000005`. A purchase is reduced to the greatest
8-place quantity affordable from available cash if nearest rounding would spend
more; no tolerance permits negative cash.

Opening cash and its `StartingCash` reconciliation baseline must already be
exactly representable at 8 decimal places and within the supported cash range.
Execution rejects unsupported balances without mutating cash, holdings, or trades;
it never rewrites historical `StartingCash` or silently normalizes existing balances.
Backtest initialization and portfolio saving throw `ArgumentOutOfRangeException`
for unsupported precision or range before creating a run or tracking a portfolio.
Accepted initial capital is copied unchanged to both execution balances. This
opening-balance policy is independent of 4-place report rounding.

Portfolio, position, trade, and normal portfolio/position snapshot state uses
the above scales. Their SQL types are `decimal(28,8)` for cash, quantities, and
notionals, `decimal(28,12)` for prices and average cost, and `decimal(28,8)` for
percentages. This preserves and increases the previous integer ranges: the
28-digit types support values below `10^20` at scale 8 or below `10^16` at
scale 12. Execution rejects values outside these ranges. Market candle prices
remain at 12 places and are not truncated to cents.

Backtest daily snapshots and final capital/profit/return remain report-boundary
values rounded to 4 places with midpoint `AwayFromZero`; their existing
`decimal(18,4)` storage is not the execution ledger. Reconcile their daily cash
and final-result values against execution state only after applying this
documented reporting rounding. Normal portfolio/position snapshots retain state
precision.

Before applying `AlignTradeExecutionPrecision`, back up the database, inspect
the generated migration SQL, and preferably apply it to a database copy first.
The upgrade only alters decimal columns; existing rows and identifiers remain,
and widening scale cannot restore fractional digits already lost by the prior
schema. Rolling back narrows the columns to `decimal(18,2)`, which rounds
fractional values and can fail for values outside the old integer range. Keep a
pre-upgrade backup if rollback may be needed. SQL Server save/reload validation
must be performed against the user's database; EF InMemory tests do not enforce
SQL decimal precision.

## Backtest portfolio limits

`MaxPositions` is the maximum number of distinct symbols with positive
quantities in the actual portfolio, not just the recommendation-list length.
The full configured universe is analyzed; entry-eligible candidates are ranked
deterministically, existing holdings occupy slots even when outside the top-N
view, and only the best candidates for genuinely available slots can open.
Fewer eligible candidates do not cause additional investment. An already
over-limit portfolio is not automatically liquidated; it cannot open new
symbols until normal exits bring it below the limit. Adding to an existing
positive-quantity holding does not consume another slot.

Each strategy's `MaxPositionWeight` remains an individual target-allocation cap.
It is applied to selected targets before execution, not by an independent daily
drift-enforcement pass. In backtests, unchanged effective targets do not cause
`Rebalance` sales solely because prices drift. Allocation sales require an
intentional reduction relative to the saved target baseline, with both targets
capped by `MaxPositionWeight`; a raw target reduction that leaves the effective
target unchanged does not qualify. Explicit unscored zero-allocation targets
still permit full exits.
Unallocated capital may remain cash. The effective
strategy configuration is stored as JSON on each new backtest run and in its
feature snapshots. Existing historical runs retain their old behavior and are
not rewritten; their results must not be interpreted as if generated under the
new portfolio lifecycle.

Current-portfolio runs persist `Position.EffectiveTargetPercent` as the last
initialized or completed selected target, capped by `MaxPositionWeight`.
`AddPositionEffectiveTarget` adds only a nullable `decimal(28,8)` column;
existing positions remain null. No market weight, average cost, or actual
holding snapshot is treated as a historical target. Apply the migration before
running the updated application. Rolling it back drops these baselines, so a
later upgrade must initialize them again.

On the first valid priced, positive selected target, initialize that baseline
without an ordinary allocation sale. Stops and supported explicit unscored
zero-target exits still execute. Omitted holdings, missing/invalid prices, and
positive-score targets whose selected weight rounds to zero leave the baseline
unchanged. New filled positions also receive their selected effective target.
Unchanged effective targets never authorize price-only allocation sales;
cash-funded top-ups remain subject to allocation and cash limits.

A genuine reduction below the saved baseline remains eligible for a partial
`Rebalance` sale (compatibility behavior, not an investment recommendation).
If no sale is needed because the position is already at or below the new
target, acknowledge the new target. If a sale is needed but is below the
minimum trade threshold, rejected, or incomplete, retain the old baseline;
the same lower target can retry on a later valid selected run. After a
successful sale fills the instruction's requested quantity rounded down to
eight decimal places, acknowledge the reduced target. Completion is compared
to that instruction, not to portfolio revaluation after other fills' cash
rounding. This accounts for execution precision without repeatedly selling
later price appreciation.
The trading engine has no liquidity-based partial-fill model; ordinary partial
position sales execute a quantized requested quantity or are rejected.
An increase in the selected target updates the baseline without requiring a
buy; it does not bypass cash or capacity limits. No additional pending-target
schema is used: the prior baseline remains until a reduction completes, and
omitted targets do not autonomously retry. Trades, quantities, cash, and
baseline changes are saved together by the post-rebalance `SaveChanges`;
failure of that save does not persist the reduction acknowledgement. Displayed
allocation sell recommendations use the same baseline guard.

New-entry eligibility keeps the existing rules: score above zero, momentum
above zero, and price above SMA5 (NVDA remains excluded). Holding eligibility
uses a separate, less demanding rule. Only when `UseHoldingConditionExit` is
explicitly enabled, one fresh close observation with `MomentumPercent <= 0`
triggers a full exit. This uses the neutral momentum boundary, not a
performance-tuned threshold; no multi-day confirmation is applied. A missing
or insufficient observation is neutral and does not trigger that exit. Stop
Loss and Trailing Stop remain independently optional and retain their existing
precedence over the holding-condition exit. The feature is backtest-only and
intended for controlled strategy experiments, not as a validated universal
liquidation policy.

Momentum is calculated as `(latest close - first close) / first close` over the
analyzer's supplied historical window. Therefore `MomentumPercent <= 0` means
the latest close is no higher than the first close in that window; it does not
mean one negative day or a loss relative to purchase price. Software tests
verify implementation behavior, not investment effectiveness.

For a controlled comparison, run an A/B evaluation without changing anything
else:

- **A:** `UseHoldingConditionExit = false`.
- **B:** `UseHoldingConditionExit = true` (explicit opt-in experiment).
- Keep strategy settings other than this flag, data, dates, modeled costs, and
  execution assumptions identical. Compare return after modeled costs,
  drawdown, trade count, rapid reentries, holding duration, cash exposure, and
  individual exit reasons.
- Reserve an untouched period for subsequent validation. Do not interpret
  results from the comparison period as proof of future performance.

Backtest closes only create signals. Exits and purchases execute at a later
valid instrument opening, with all eligible sales before buys. A pending or
partial sale does not release a position slot; only a completed full sale
does. Before each new-symbol buy, the execution phase rechecks actual holdings,
including earlier buys that day. If a valid opening arrives but no slot is
free, the purchase intent is rejected once; a subsequent fresh qualifying close
may create a new intent. If no valid opening exists, the intent remains
deferred and reserves its slot. At a later fresh close, it remains eligible
only if it still satisfies entry rules and wins a slot when ranked together
with fresh new candidates; otherwise it is canceled. Actual holdings are not
replaced by that competition. Fresh qualifying targets refresh deferred scores
before allocation normalization. A changed score or allocation advances the intent's signal date
to that close, requiring a strictly later opening; unchanged targets retain
their original eligibility. Missing observations preserve the previous score
and reservation. Exit and blocked-purchase diagnostics include the triggering rule
and symbol. Falling out of the recommendation list or a better candidate
appearing does not itself signal liquidation.

The allocation target is capped before execution. Its bounded rounding tolerance
is `price × 0.000000005 + 0.000000005` for each buy; this is not permission to
exceed the allocation cap by a fixed percentage. Any unspent requested amount,
including the difference between requested and executed notional, remains as
cash. Subsequent price appreciation changes portfolio value, not trade cash
accounting.

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
exception for empty charts and HTTP 404. It also reports HTTP 400 as
`InvalidOperationException("Yahoo Finance Server Error BadRequest")`, including the
reported PLTR pre-listing failure. Neither message alone is **proof of no data**.
Only these exact, non-derived `InvalidOperationException` shapes on a bounded request
with a nonblank symbol trigger an additional chart request for the identical symbol
and UTC range (Yahoo's end is exclusive).
Confirmation uses a browser-compatible user agent. If Yahoo requires authentication
(HTTP 401), it acquires a session crumb on the same cookie-enabled client and retries
the identical chart range once. Cookies/crumbs are never persisted in the database.
It confirms emptiness only for:

- HTTP 200 with no chart error, one matching-symbol result, empty/absent timestamps,
  and an empty quote object or empty quote arrays.
- HTTP 404 with `chart.result=null`, error code `Not Found`, and the exact
  `Data doesn't exist for startDate = <requested Unix start>, endDate = <requested Unix end>`
  description, returned by the chart endpoint for the requested symbol.
- HTTP 400 with `chart.result=null`, error code `Bad Request`, and the same exact
  range-specific description, **plus** a successful symbol validation (below).

Yahoo returns the 400 shape for a valid symbol queried wholly before its listing.
Captured live on 2026-10-20 for PLTR `period1=1557446400&period2=1557878400`
(May 10–14, 2019 inclusive):

```json
{"chart":{"result":null,"error":{"code":"Bad Request","description":"Data doesn't exist for startDate = 1557446400, endDate = 1557878400"}}}
```

That body echoes only the dates, not the symbol, so it cannot by itself distinguish
a pre-listing range from an unknown ticker. For this case only, the provider makes
one more bounded chart request for the same symbol (`range=1d&interval=1d`,
same session handling) and confirms emptiness only if it returns HTTP 200,
`chart.error=null`, exactly one result whose `meta.symbol` matches
(case-insensitively), and a numeric `meta.firstTradeDate` (Unix seconds) at or after the
requested exclusive end. In other words, the whole requested range must precede Yahoo's
first trade for a symbol Yahoo currently resolves. Unknown tickers (Yahoo 404
`No data found, symbol may be delisted`), missing/null/non-numeric `firstTradeDate`,
a first trade before the requested end, a mismatched symbol, and any non-200 or
malformed validation response remain failures. Neither the listing date alone nor the
status, code, or a message substring is enough; both checks must pass.

Any other HTTP 400 remains a failure, including a body with a different code, any
date mismatch, extra text, a non-null result, or malformed JSON. Invalid requests,
unclassified BadRequest responses, generic 404/delisting messages, authentication failures after the bounded session retry,
network errors, rate limits, server errors, malformed responses, and unknown errors
remain failures. There is no weekend/calendar guess
or catch-all empty fallback. Confirmation failures also propagate. Faulted loads
are evicted from the in-process cache so later calls can retry in the same scope;
there is no general automatic retry loop.
If saving or committing market data fails, only that upsert's pending candles and
coverage are detached. Unrelated pending changes remain retryable; tracked changes
are accepted only after the transaction commits successfully.
Confirmed empty ranges (including pre-listing ranges) add coverage without fake
candles. Earlier persisted candles still return, subject to the simulated-date
cutoff. Benchmarks use the same retrieval path. Coverage suppresses repeat requests
only within the confirmed interval; the next uncovered trading date is still fetched.

The BadRequest exception mapping was verified against the installed
OoplesFinance.YahooFinanceAPI 1.7.1 package's
[`DownloadHelper.DownloadRawDataAsync`](https://github.com/Ooples-Finance-LLC/OoplesFinance.YahooFinanceAPI/blob/4a59f93a95f1cc24cc1c5ed8f85708425d06d9ff/src/Helpers/DownloadHelper.cs#L91-L100).
The client discards the failure response body and exposes neither a typed HTTP status
nor that body on this exception. A live bounded PLTR chart probe for
`period1=1557446400&period2=1557878400` (May 10–14, 2019 inclusive) could not resolve
Yahoo's hostname in the implementation sandbox; the body above was captured by a user
with `curl.exe`. Yahoo's unknown-ticker and `firstTradeDate` shapes could not be
re-probed live from the sandbox; tests model them on commonly observed chart
responses, and any deviation fails closed (no coverage).
Deterministic regressions reproduce the exact client exception and the captured 400
body and require structured confirmation. They cover rolling starts May 10–15, 2019 for simulated
September 25–30, 2020: after confirming May 10–14 once, persisted coverage suppresses
all overlapping gap requests while candles from September 30 onward stay unchanged.
If Yahoo returns an unclassified 400, or the symbol validation fails, the interval
remains uncovered and retryable; do not manually certify it based solely on a known
listing date.

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
Failed requests are not memoized; retry after resolving a provider or database failure.
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
| `MaxPositions` | Maximum number of distinct positive-quantity positions; must be greater than zero. |
| `MaxPositionWeight` | Maximum position weight, from greater than zero through 100. |
| `StopLossPercent` | Optional percentage loss from average entry price; greater than zero through 100. Omitted or `null` disables Stop Loss. |
| `TrailingStopPercent` | Optional percentage drawdown from the highest observed execution price since entry; greater than zero through 100. Omitted or `null` disables Trailing Stop. |
| `UseHoldingConditionExit` | Opt in to the backtest holding exit when a fresh observation has momentum at or below 0% (default `false`; controlled experiments only). |
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

Set `"StopLossPercent": 10` on a strategy to exit an entire position when its available end-of-day close is at or below 90% of its average entry price. Current portfolio simulations execute these sells before rebalancing; backtests queue a close-evaluated exit for a later valid instrument open, as described in [Backtest causal execution](#backtest-causal-execution). Both persist `ExitReason.StopLoss` on actual fills. A stopped symbol is not repurchased in the same step, but may be recommended again on later steps. Missing or non-positive prices, non-positive entry prices, and non-positive quantities do not trigger a stop. Backtests evaluate fresh closes, including held symbols outside the requested universe, without requiring enough candles for scoring. No intraday fills or execution at the threshold price are modeled.

Set `"TrailingStopPercent": 10` to sell an entire position when the available close is at or below 90% of its `HighestPriceSinceEntry`, even if it remains above entry price. The high starts at the purchase price, increases with observed closes, and is persisted between current portfolio simulations. Backtests track entry execution price and completed closes only, never the daily `High`, and queue triggered exits for a later valid open. Additional buys and partial sells retain the high; a new position after full liquidation starts a new high. Tracking continues when the stop is disabled, including in current simulations with fewer than five scoring candles: available latest closes are retained regardless of either exit setting. Both modes persist `ExitReason.TrailingStop`, skip missing/non-positive prices and non-positive quantities, and prevent same-step repurchases. Stop Loss takes precedence when both stops trigger. Like Stop Loss, evaluation uses available daily closes, not intraday highs or threshold fills. Current simulation requires an available price for the held symbol; backtests also fetch held symbols outside the requested universe without requiring scoring history. The migration initializes existing positions from the greater of their known average entry/current price (or zero if neither is positive); historical highs before this migration cannot be reconstructed.

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

### Backtest causal execution

Backtests use run-local, ephemeral pending intents per symbol. Each fresh run
starts with isolated pending state. The run configuration column requires the
`AddBacktestRunStrategyConfiguration` migration; existing runs remain nullable
because their effective configuration cannot be reconstructed.

- **First date and warmup:** history before `StartDate` warms indicators only;
  it cannot generate pre-start orders. The first simulated date marks available
  closes and generates after-close signals only, with no opening fills.
- **Opening phase:** an intent can execute only at a valid actual candle `Open`
  for that instrument on a date strictly after its signal date. There is no
  fallback execution price, weekday rule, or inferred trading session. A missing
  candle or invalid `Open` leaves the original intent pending and reserves its
  slot. Fresh closing observations requalify pending purchases; loss of entry
  eligibility or capacity cancels them. A close-evaluated exit overrides a
  purchase target and remains sticky until its eligible opening attempt.
- **Attempts and allocation:** an eligible opening attempt consumes the intent
  once, even if cash or the minimum trade threshold prevents a fill. A later
  fresh close may generate a new target. Sales execute first; an unexecuted or
  partial sale does not free a slot. New symbols are checked against actual
  positive-quantity holdings before each buy; an over-limit portfolio is not
  automatically liquidated. Opening allocation valuation uses eligible opening
  prices and prior known values only, never that date's closing candle fields.
- **Closing phase:** only after opening trades may closing candle fields be
  used for analysis, closing-price marks, and stop evaluation. Existing holdings
  with an invalid or missing current close restore their prior pre-open valuation.
  A newly opened position without a prior mark retains its entry execution `Open`
  if the close is invalid. An invalid close generates no signal or stop. Trailing highs
  use completed closes and entry execution prices, not daily `High` or an
  assumed intraday price path. Signals and feature snapshots retain the signal
  day's date, not the later execution date.
- **Rejected candles:** a positive, normalizable `Open` remains available for
  an eligible pending intent even when another OHLCV field makes the full candle
  invalid. The rejected candle is not persisted, does not certify data coverage,
  and is excluded from analysis and closing-price marks; the missing range can be
  fetched again later. Existing committed snapshots remain authoritative.
- **Gaps and recommendations:** signals/features require a fresh candle.
  Display rankings retain latest known observations only for symbols still in
  the current requested universe; symbols removed from that universe are
  pruned from the cache. New purchase intents require a fresh candidate and
  available slot. Held symbols are analyzed when data permits, even outside the
  requested universe. Missing/insufficient holding observations do not trigger
  holding exits. Omitting a held symbol from the top-N view does not create a
  liquidation target; a configured stop or explicit holding-condition violation
  can still signal an exit.
- **End and reporting:** pending intents remaining after `EndDate` are discarded;
  there are no fills beyond the requested period. Daily snapshots cover every
  calendar date, including non-trading dates. `TradesExecuted` counts actual
  fills; `RebalanceExecuted` means actual rebalance trades occurred, not merely
  that recommendations were generated, and risk-only exits do not set it.

Commissions, spread, slippage, universe selection, and corporate-action handling
remain unchanged limitations. Persisted older results are not rewritten.
Historical returns are not correctness targets: causal execution may change
returns in either direction, with no claim that a correction improves or lowers
performance.

#### Rerun and reconcile Q1/full-year 2020

These commands use the existing `Program.cs` variant/date options and
`StrategyComparisonCommandLineParser` / `StrategyComparisonCsvCommandLineParser`;
they are instructions, not evidence of manual runs:

```bash
dotnet run --project IntelligentTradingSimulator.App --configuration Release -- \
  --run-variants --backtest-start=2020-01-01 --backtest-end=2020-03-31 \
  --comparison-sort-by=ReturnPercent --export-comparison-csv=comparison-Q1-2020.csv

dotnet run --project IntelligentTradingSimulator.App --configuration Release -- \
  --run-variants --backtest-start=2020-01-01 --backtest-end=2020-12-31 \
  --comparison-sort-by=ReturnPercent --export-comparison-csv=comparison-full2020.csv
```

Each command creates new runs for every configured strategy; keep configuration,
stored candle inputs, and universe fixed when comparing results. Record the new
run IDs and input/configuration provenance separately from older runs. Comparison
CSVs summarize outcomes; they do not prove execution timing. For each run, use
`--backtest-run-id=<id> --export-backtest-snapshots-csv=<path>` to inspect snapshots
and load persisted `Portfolio.Trades` to reconcile fill dates, prices, quantities,
cash, and exit reasons against actual instrument opens and prior signal-day
observations/features. Check first-day zero fills, pending gaps, sells before
buys, closing marks, calendar-day coverage, actual-fill counts, and absence of
post-end fills. Snapshot-only export does not regenerate execution or repair old
results; investigate discrepancies from inputs and event order rather than
trying to reproduce a previous return.

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

This records reasons supplied by callers. Stop-loss and trailing-stop settings
are described above; backtests use the delayed execution rules in
[Backtest causal execution](#backtest-causal-execution).

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

From the repository root, use the SDK configured in `global.json` to restore, verify that the Release build succeeds, and run the test suite. A successful build exits with code 0. The tests use in-memory databases and do not require a running SQL Server or Yahoo Finance access:

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

## Read-only diagnosis of skipped orchestration jobs

### Existing test task mapping failure

For the retained Issue #227 mapping hold, **Agent task mapping diagnostics
(read-only)** is a separate manual workflow restricted to its definition on
`master` with activation explicitly `false`. It uses the protected
`agent-orchestration` environment and its user credential for exactly one GET
of the saved task `e4743d38-6b27-49cd-a272-83b34465a808`. No task creation,
checkout, CI dispatch, state write, or recovery operation is performed.
It additionally reads the fixed PR #232 and resolves the task's pull artifact
with a fixed GraphQL query using a separate workflow token granted only
`contents: read` and `pull-requests: read`. GraphQL uses POST for a query,
not a mutation. The report compares branch refs, REST/global identities and
GraphQL identity; HTTP failures are reported by status, without response bodies.
Unevaluated checks are omitted, not treated as passing. Read-only permissions
and a current snapshot do not reproduce the original run's exact conditions.
Identity matches require positive Int64 database IDs and populated global IDs;
GraphQL database IDs may also be decimal BigInt strings. Missing values never
match each other. PR/GraphQL body timeouts are reported as transport errors
while preserving earlier results. Pull artifact and REST global IDs are classified
as `missing`, `null`, `not-string`, `empty`, `too-long` (over 256 characters),
`whitespace`, or `valid`, without logging their values or exact lengths.
Database-ID validity and database/global-ID REST matches are reported separately;
the combined match still requires both identities to match. Artifact ID validity
and global-ID status are retained even if the subsequent PR read fails.
These diagnostics do not relax parser validation or authorize a mapping.
An `empty` artifact global-ID status still yields a false combined global/REST
match; that diagnostic alone does not reject the separately validated
branch-plus-database-ID mapping path described below.
Run diagnostic script regressions locally
with `python .github/scripts/test_task_mapping_diagnostics.py` (no network or
credentials required).
Run it only with separate operator approval, while both live workflows remain
disabled. The report contains identity-match booleans and allowlisted field
shapes, not raw API content. It describes the current response, not the lost
snapshot from the failed run.

New mapping holds include `code=mapping/<stage>/<exception-type>` in their
reason, without exception text or API values. Existing holds are not rewritten.
These diagnostic events use a `v2` identity so replay cannot reuse an old event
ID with a changed reason payload. The identity includes the validation stage
and exception type as well as the response digest, so different failures on
the same task response remain distinct while identical failures replay stably.
Use that stage and the response-shape report to create a faithful regression
fixture before changing accepted artifact formats. Do not ignore unknown
artifacts, manually assign a PR, or clear the single-pass hold as part of diagnosis.

### Activation gate checks

Keep both live workflows manually disabled, remove `agent:ready` from the test
Issue, and keep `ORCHESTRATION_ACTIVATION_APPROVED=false` while diagnosing.
Run **Agent gate diagnostics (read-only)** manually from `master` in Actions.
It has no token permissions, checkout, credential environment, secret references,
or live orchestration commands. It writes only its own logs and job summary.

The summary reports each repository-variable comparison separately, including
the activation/Issue/cap gates and the test CI pins. It also shows this diagnostic
run's event and workflow refs. It does not reconstruct the context or variable
values of a previous Issue-triggered run, and its own workflow ref names the
diagnostic file, not the reconciler file. A successful diagnostic job means only
that reporting completed; false comparisons do not fail it or authorize a start.
The activation comparison should be false while the test is stopped.

The definition-SHA comparison uses this run's commit. A newly merged PR changes
that SHA, so a previously valid `ORCHESTRATION_DEFINITION_SHA` can report false.
Before a separately approved live start, pin the reconciler to the reviewed
current `master` commit; do not move the protected CI tag or change its SHA just
to make this diagnostic green. Do not retry the live start until the discrepancy
is understood.

## Agent state machine: implementation and temporary test activation gates

### Main workflow and configuration ownership

The target lifecycle is `agent:ready` -> completed coding -> exact-head green CI
-> Copilot PR review -> fixes and fresh CI/review when needed -> final human
review. Nothing merges automatically. The isolated status-revocation experiment
is a separate, currently stopped test, not the main workflow or its activation
procedure.

The implementation is not yet a proven automatic review loop: the current
Copilot review API adapter records submitted output as `NeedsHuman`. It cannot
infer acceptance from "no issues", `COMMENTED`, or the absence of inline comments.
Automatic review classification requires a verified request-round/outcome
contract; custom instructions alone do not provide that API guarantee. Until
that gap is resolved, a bounded test must explicitly include a human
`classify-review` step and must not be reported as a fully automatic happy path.

After exact-head green CI, the adapter uses `GITHUB_TOKEN` and REST API version
`2026-03-10` to call
`POST /repos/{owner}/{repo}/pulls/{pull_number}/copilot-code-review`.
The [endpoint documentation](https://docs.github.com/en/rest/pulls/copilot-code-review?apiVersion=2026-03-10)
defines `201` with a request node ID and `PENDING`: this acknowledges a request,
not review completion. `409` means an existing review is pending on the PR;
it does not identify that review's commit or prove this workflow requested it.
The adapter records that distinction and does not issue another request.
The result must independently match the mapped PR, exact head SHA and pinned
Copilot author, be newer than the saved baseline, and await human classification.
The REST review list does not expose a proven request-to-submission round token.
An earlier executing, uncertain or successful request in canonical state for
the same PR and commit prevents a new POST; a definitive rejection can be
handled through the existing guarded recovery process.

Lifecycle state distinguishes requested, pending, completed, failed and timed
out; the default deadline is 15 minutes. Set optional repository variable
`ORCHESTRATION_POC_REVIEW_TIMEOUT_MINUTES` for the PoC or
`ORCHESTRATION_REVIEW_TIMEOUT_MINUTES` for the main driver to an integer 1–30.
Timeout is checked while polling, not by a separate timer. The worker must be
running or invoked again to observe it. A timeout blocks the issue and does not
cancel remote work.

For new Issues, the main driver sets a maximum of five repair tasks by default;
set `ORCHESTRATION_MAX_REPAIR_TASKS` to 1–8 before admission to choose another
limit. This caps CI, review and feedback repairs together, and therefore bounds
the number of review/fix cycles. Existing admitted Issues retain their saved
limit; an older Issue without one is not retroactively capped. The isolated PoC
remains fixed at one repair task and two reviews.

### Artifact-only Copilot review E2E test for Issue #265

Issue #265 is a fresh, one-shot test profile with state isolated from #261,
#227/#237 and the #257/#259 PoC. Its task is deliberately mechanical: add only
`orchestration-e2e-artifacts/issue-265.txt`, containing exactly
`E2E artifact for Issue #265` and one trailing newline. Do not change
application, test, workflow or documentation files. Review the generated PR
diff against that exact contract. Keep the PR unmerged; the file is a disposable
test artifact, not a product feature.

The Issue is open and unlabelled. The setup does not dispatch the workflow,
change repository variables/secrets, create the managed-CI tag, request review,
or merge a PR. After the setup PR is merged, record its full lowercase SHA and
set `ORCHESTRATION_DEFINITION_SHA` to that SHA,
`ORCHESTRATION_TEST_ISSUE=265`,
`ORCHESTRATION_TEST_MAX_REPAIR_TASKS=1`, and leave
`ORCHESTRATION_RETIRED_TEST_ISSUE` unset. Keep
`ORCHESTRATION_ACTIVATION_APPROVED=false`; set
`ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED=true` only for the one-time setup.
The actor ID must identify the human operator. Review the existing
`agent-orchestration` environment protection rules before dispatch.

From `master`, manually run **Copilot review artifact E2E bootstrap (opt-in)**
with Issue `265` and confirmation `BOOTSTRAP-REVIEW-E2E-265`. The workflow
grants only `actions: read`, `issues: read`, and `contents: write`; it uses
`GITHUB_TOKEN`, does not receive the task credential, and creates only the empty
`refs/heads/agent-state-review-e2e-265-v1` with a definition-bound manifest.
Bootstrap refuses an existing state ref or a nonempty state and cannot reset
or retry the profile. Inspect the run and receipt, then set
`ORCHESTRATION_REVIEW_E2E_BOOTSTRAP_APPROVED=false`.

Before enabling execution, create and protect the immutable lightweight tag
`agent-managed-ci-test-265-v1` at the reviewed merge commit; never move it. Set
`ORCHESTRATION_CI_REF=refs/tags/agent-managed-ci-test-265-v1` and
`ORCHESTRATION_CI_DEFINITION_SHA` to its exact target. Reverify existing review,
CI, task and cost gates with read-only diagnostics; do not blindly copy old
attestations. Only after all prerequisites pass, separately approve
`ORCHESTRATION_ACTIVATION_APPROVED=true` and add `agent:ready` to #265.

Observe coding, exact-head managed CI, and the Copilot review result. Check that
the PR changes exactly the one specified artifact. This profile is single-pass:
it does not launch a repair task or a second review. Stop on an unexpected file,
failed/uncertain API stage or actionable review feedback; do not retry an
uncertain review POST or merge the test PR. After the run, turn activation and
bootstrap approval off, remove the readiness label if present, unset the
bounded-test variables, and retain the isolated state/evidence. This live test
may consume existing Copilot/Actions quota.

The workflow grants `pull-requests: write`; `GITHUB_TOKEN` is a GitHub App
installation token and the official OpenAPI marks this endpoint as enabled for
GitHub Apps. The endpoint documentation does not explicitly state the minimum
permission or confirm this particular Actions token's entitlement: verify it
in a separately authorized live run. Copilot review access, policy, licensing,
quota and billing must also be verified for the requesting actor/repository.
No PAT substitution is implied. [Actions suppresses most workflow events caused
by GITHUB_TOKEN](https://docs.github.com/en/actions/concepts/security/github_token#when-github_token-triggers-workflow-runs);
this driver polls the reviews API rather than depending on a review-triggered
workflow, but the suppression rule alone does not establish whether Copilot
itself starts.

Documented `401`, `403`, `404`, `422` and exhausted `429` responses are recorded
as rejections; retry `429` at most twice, within the bounded delay, after
revalidating the candidate. A lost or malformed acknowledgement, `503`, other
unexpected response, or failed confirmation write remains uncertain and
lookup-only: do not automatically POST again. A `503` can come from an
intermediary, so it is not assumed to prove that no request was created.

The offline integration test
`OneRepairPoCCompletesCodingCiReviewRepairCiReviewAndStopsForHumanApproval`
demonstrates one capped repair cycle using mocked GitHub APIs and
`MaxRepairTasks=1`. It verifies exact-head CI/review after the repair, finding-
specific human resolution, and a final human-review handoff without merge. This
is a code-path PoC only: it does not verify live credentials, repository
protection, workflow pins, or deployed Actions configuration. The existing
bounded Issue #237 profile remains single-pass and cannot run this repair cycle.

### Bounded live coding/CI/review PoC for Issue #257

The opt-in `agent-poc.yml` driver reuses `GitHubOrchestrationHost`, the existing
task/CI/review adapters and reducer. It is not a second orchestrator and does
not activate the main workflow or the status-revocation experiment. Its CLI mode
is `--github-live --github-poc --execute`. These source changes alone do not
provision resources or authorize a live run.

The current profile is fixed to Issue #257, PR base `master`, and separate state
`refs/heads/agent-state-poc-257-v1`. Both driver and CI execute from `master`
at the configured reviewed definition SHA; CI checks out the agent's exact task
head separately. The task creates a draft PR against `master`. If `master`
advances during the attempt, the profile stops rather than executing an
unreviewed definition. The driver validates the repository, configured human
actor, original run actor, attempt 1, definition and generation before
admission. Re-runs are rejected.

The canonical manifest pins those identities and an expiry at most 72 hours
away. Its store permits one coding task, at most one review repair task in total,
at most two managed CI dispatches and two review requests. CI build/test failure,
infrastructure failure or ambiguous API outcomes stop the attempt. Unclassified
human feedback requiring a separate repair also stops the attempt. Changing configuration or extending expiry
cannot silently reuse bootstrapped state. Missing state is an error, not a new
attempt. Do not delete state or re-bootstrap to retry.

The retired v1 state is history only. Its uncertain task launch can be closed
only by the separate `agent-poc-recovery.yml` command, which never calls the
Agent task API. That command accepts only the recorded v1 state revision and
requires an explicit human attestation; it records what the operator asserts,
not independent proof that no task was created. The #249 v2 attempt completed
coding and CI but stopped on an uncertain review request. Preserve both #249
state refs and PR #256 unchanged; this profile neither resumes nor resolves them.
The new #257 attempt bootstraps its own v1 ref with a new manifest and current
definition/expiry. Never reuse an earlier attempt's state.

Unlike the old bounded profile, this profile does not set `SinglePassTest`.
It does require the mapped PR to stay draft and based on `master`.
It deliberately does not publish required commit statuses, does not test
status revocation, and is not evidence that production branch protection works.
It never calls the merge API. Passing CI, review and final human handoff are
the scope of this PoC, not deployment or automatic merge.

#### Prepare once, after implementation review

Keep the legacy/main execution workflows disabled and their variables unchanged.
Do not use the resources or state of #227, #237 or the status experiment.

1. Review and merge this implementation. Record the full lowercase `master`
   commit SHA; keep `master` unchanged during the attempt. Do not merge the
   generated task PR.
2. Verify that `master` is admitted by the existing `agent-orchestration`
   environment's deployment policy.
   Reuse its `ORCHESTRATION_USER_TOKEN` secret; do not copy credentials into new
   environments or repository variables. Verify task entitlement, review API
   access, environment protection, effective workflow permissions, and agreed
   AI/Actions cost limits independently.
3. Obtain the driver and CI workflow IDs from GitHub after merge, and set the
   repository variables below. Approval flags are operator attestations, not
   measurements. Do not set them true merely because local tests passed.

| Repository variable | Required value |
| --- | --- |
| `ORCHESTRATION_POC_APPROVED` | `false` until the bounded live attempt is separately authorized |
| `ORCHESTRATION_POC_BOOTSTRAP_APPROVED` | `false` except for separately authorized one-time bootstrap |
| `ORCHESTRATION_POC_RECOVERY_APPROVED` | `false` except for a separately authorized one-time v1 recovery |
| `ORCHESTRATION_POC_ACTOR_ID` | Numeric ID of the authorized human owning the task credential |
| `ORCHESTRATION_POC_WORKFLOW_ID` | GitHub ID of `.github/workflows/agent-poc.yml` |
| `ORCHESTRATION_POC_CI_WORKFLOW_ID` | GitHub ID of `.github/workflows/agent-poc-ci.yml`; distinct from driver |
| `ORCHESTRATION_POC_DEFINITION_SHA` | Reviewed current `master` commit containing the driver, CI and recovery workflows |
| `ORCHESTRATION_POC_EXPIRES_AT` | Agreed UTC deadline, no more than 72 hours away |
| `ORCHESTRATION_POC_CREDENTIALS_VERIFIED` | `true` only after verifying separate credentials and their protected environment |
| `ORCHESTRATION_POC_TASK_ENTITLEMENT_VERIFIED` | `true` only after verifying task API entitlement |
| `ORCHESTRATION_POC_TRUSTED_DEFINITION_VERIFIED` | `true` only after reviewing the driver and CI at the configured SHA |
| `ORCHESTRATION_POC_NO_ADDED_CHARGES_VERIFIED` | `true` only after confirming the agreed cost controls |
| `ORCHESTRATION_POC_REVIEW_POLICY_VERIFIED` | `true` only after verifying Copilot review access/policy |
| `ORCHESTRATION_POC_AUTOMATIC_REVIEWS_DISABLED` | `true` only after verifying native automatic reviews will not duplicate managed requests |
| `ORCHESTRATION_POC_REVIEW_TIMEOUT_MINUTES` | Optional integer 1–30; defaults to 15 and is part of the PoC manifest |

Use the shared environment's approval rules for each driver invocation. This
does not imply an independent second-person approval if the maintainer may
approve their own run. The credential-bearing driver only builds and executes
the trusted definition, never task-head code. The CI job checks out the captured
task head with contents-read permission, no task secret and no state/status
writes. Same-repository workflow credentials are not ref-scoped tokens; scope
is enforced by the reviewed driver/adapters/store.

#### Authorized live sequence

After separate authorization, enable only the two new PoC workflows if needed
and set `ORCHESTRATION_POC_APPROVED=true`. All dispatches select `master` and supply
`confirmation=POC-257-ONE-REPAIR-NO-MERGE`.

1. Temporarily set `ORCHESTRATION_POC_BOOTSTRAP_APPROVED=true`. Dispatch the
   driver with `operation=poc-bootstrap` and `issue=257`. This creates only the
   separate empty canonical state and its manifest. Verify the artifact and
   state ref, then set bootstrap approval back to `false` before starting.
2. Check the coding requirements in Issue #257. Add `agent:ready` only when
   ready to authorize its coding task, then start the driver once:

   ```bash
   gh workflow run agent-poc.yml \
     --repo ExarKun1983/IntelligentTradingSimulator \
     --ref master \
     -f issue=257 -f operation=poc-start \
     -f confirmation=POC-257-ONE-REPAIR-NO-MERGE \
     -f repository_generation=1 -f issue_generation=1
   ```

3. The worker observes task completion, captures/verifies its candidate head,
   dispatches exact-head CI, then requests Copilot review after green CI.
   A driver invocation is bounded to 30 minutes. `TimeLimit`/`IterationLimit`
   is not completion: inspect canonical state and use a **new** manual
   `operation=resume` invocation for observation/continuation, not another
   `poc-start` and not GitHub's "Re-run jobs". There is no schedule or event
   wake for this profile. Do not resume uncertain writes speculatively.
4. Copilot review output produces `NeedsHuman`. Read the review on the captured
   SHA and take `CurrentHandoffId`, `Round`, PR and SHA from canonical state
   or the artifact. Dispatch `operation=classify-review` with those exact
   `handoff`, `round`, `pull`, `sha` values, an audit `reason`, and
   `classification=blocking` or `accepted`. For blockers, supply a JSON array
   in `findings`, e.g.
   `[{"Id":"review-finding-1","Description":"Actual review finding","Evidence":"Exact review comment URL"}]`.
   Do not infer acceptance from the absence of comments or manufacture a finding
   to force a repair.
5. A blocking classification can launch the one repair on the same PR branch.
   The resulting completed-task head must pass fresh CI and review. For final
   acceptance of previously recorded blockers, provide finding-specific
   `responses`, e.g.
   `[{"FindingId":"review-finding-1","Evidence":"Verification on the repaired SHA","Resolved":true}]`.
   A second required repair stops on the persisted budget; it never creates
   a third task.
6. Completion means phase `HUMAN_REVIEW`, explicitly accepted review, current
   exact-head green CI and an unmerged draft PR. The artifact reports
   `Completed=true` only after revalidating the task and live PR head, draft
   status and `master` base at that handoff. A green driver job by itself is not
   proof of completion. Preserve canonical state and evidence; set
   `ORCHESTRATION_POC_APPROVED=false` after the attempt and leave bootstrap false.

Every invocation saves `agent-poc-result.json` as a seven-day Actions artifact
with phase, stop reason, task identities, current CI/review boundaries and effect
receipts. Full review evidence and classification history remain in canonical
state. The receipt also includes a bounded `TaskLaunchDiagnostic` containing the
launch outcome, HTTP status, validated GitHub request ID, exception category/type,
response root kind and presence of required task fields. It never includes the
request prompt, credentials, response body or exception message. A missing HTTP
response has no status/request ID; diagnostics do not resolve an unknown outcome
or authorize retry.

`ReviewRequestDiagnostic` additionally records the review intent/Issue, last
stage, whether execution reached the POST call, attempt, HTTP status, validated
GitHub request ID, response state/request node ID, bounded retry delay, stable
failure reason and sanitized exception category/type. Stages distinguish preflight,
durable boundary save, post-save revalidation, POST/body read and acknowledgement save.
`PostAttempted=true` means the POST call was entered, not proof GitHub received
it. `Outcome=confirmed` means the acknowledgement was saved, not that review
completed. In historical runs using the old endpoint, a 201 response followed by `acknowledgement_reviewers_validation`,
a nonzero reviewer count and `HasVerifiedReviewer=false` means the local
validator rejected the returned reviewer list; it does not prove that GitHub
did not start Copilot. A missing status with `transport` indicates no response
headers were observed. Unexpected HTTP responses may also be ambiguous.

Each stage is written to the sanitized `agent-poc-diagnostics.log`, displayed
in the Actions step and uploaded alongside the JSON even when the driver fails.
The JSON retains the latest review request diagnostic (the log includes earlier
rounds) and each effect's failure code. No raw response body, exception message,
prompt or credential is logged. Diagnostics never change request success rules,
resolve an uncertain write, or authorize an automatic retry. A null review
diagnostic means no review execution was entered during that invocation;
inspect the phase, hold reason and effect failure codes rather than inferring
that a POST was rejected.

Errors report a sanitized error type and the state ref to
inspect; they never automatically repeat a POST. Use `operation=pause` with a
reason to save a hold while authorization is still valid. Expiry stops further
driver effects, not an already submitted remote task or CI run. Any further recovery after an
ambiguous write or expiry needs a separate operator decision, not state
replacement or an automatic expiry extension.

### Main workflow diagnostics and recovery

Use the existing **Agent gate diagnostics (read-only)** workflow as the main
configuration dashboard after its definition is reviewed/merged. It reports
boolean comparisons for credentials/entitlement, cost, review, CI, status
prerequisites, identity, definition pins, test scope and inactive isolated-test
approvals. It never uses the task secret or changes variables, state, statuses,
review requests or workflow enablement. A green diagnostic job means the report
was produced, not that every comparison passed or activation was approved.
Operator attestations are not runtime permission measurements.

Do not add another environment or copy these variables between scopes:

| Existing group | Owner and action |
| --- | --- |
| Repository `ORCHESTRATION_*`, excluding `ORCHESTRATION_STATUS_TEST_*` | Main workflow. Change only under its reviewed activation plan. Keep `ORCHESTRATION_ACTIVATION_APPROVED=false` while repairing/testing offline. |
| Repository `ORCHESTRATION_STATUS_TEST_*` | Isolated status experiment only. Keep its `APPROVED` and `BOOTSTRAP_APPROVED` false; do not copy its refs, SHA or approvals into the main workflow. |
| Environment `agent-orchestration`: `ORCHESTRATION_USER_TOKEN` | Main task credential. Keep secret; diagnostics must not access it. |
| Environment `agent-status-revocation-test`: `ORCHESTRATION_STATUS_TEST_USER_TOKEN` | Isolated-test credential. Do not copy it or create environment-variable overrides. |

Repository variables are required for job-level `if` expressions, before
environment access. An absent value is not an approval and is not the same as
an explicit `false` where the guard requires it. Do not delete historical
variables, tags or state as "cleanup" while immutable workflows can still refer
to them. First map consumers and disable the obsolete execution path; removal
needs a separate reviewed migration. The uncertain-launch recovery adds the single
`ORCHESTRATION_POC_RECOVERY_APPROVED` variable. It does not migrate or remove
existing variables.

Before any new live test: reconcile the saved `Unknown` review request holding
Issue #237 only through the audited operator action described below; never POST
the review request again or erase history. After this reviewed change is merged,
set `ORCHESTRATION_DEFINITION_SHA` to that merge commit before the dashboard or
manual command. Leave activation false until the exact command and one bounded
attempt are separately approved. The current workflow scope and single-pass
restrictions remain in force, so neither recovery nor `request-changes` makes
the old single-pass fixture eligible for repair-task execution.

#### Audited abandonment of an uncertain Copilot request

Normal and recovery runs share a repository-wide concurrency group with
`cancel-in-progress: false`. Before the first recovery using this definition,
disable new normal triggers and wait for every run started from an older,
unserialized definition to finish. Do not recover while an older executor could
still send a review POST; merging this workflow does not serialize existing runs.

The request-review endpoint has no idempotency key or request/job ID. An absent
pending reviewer and absent submitted review do **not** prove that an earlier
POST never happened. For a state held only by an ambiguous request, an authorized
operator may choose workflow operation `abandon-uncertain-review` only after
the exact current PR/head, candidate task evidence, all requested-reviewer pages
and review history are re-read. Pagination failures
or capacity limits retain the hold. The operation refuses changed/closed refs,
any pending Copilot request, any Copilot review history, a confirmed request
boundary, or other unfinished work. It performs no review POST.

The dispatch must provide the canonical review-intent ID, current PR and SHA,
an audit reason, and `acknowledge_possible_prior_submission=true`. It records
the read evidence, operator identity, time, reason, and explicit risk acceptance
in canonical state; marks only that uncertain effect failed with
`OperatorAbandonedUnknownReview`; leaves the Issue blocked and admission released.
This is an explicit choice to abandon a request whose past delivery remains
unknown, not proof it was never sent. It does not reactivate or launch work.
Starting a new coding round requires a separate authorized `reactivate` command,
which is a distinct operator decision and task identity. If the read finds any
pending request/review or cannot verify the exact identities, stop and retain the
hold. Replaying the same abandonment command is idempotent and never retries the
review. This one recovery-only dispatch is allowed while
`ORCHESTRATION_ACTIVATION_APPROVED=false`; it does not set activation or permit
task execution. Environment protection, the trusted workflow pin, and explicit
operator dispatch still apply. The CLI classifies the recovery command before
loading activation-backed pipeline gates; settings and the host require a
separate recovery approval based on verified credentials, trusted definition and
cost controls. This approval never enables task launch or automatic wake-ups.
Normal execution and `reactivate` still require activation and pipeline approval.

#### Audited closure of Issue #249's uncertain v1 task launch

The v1 task-create POST had an ambiguous outcome. Do not retry it, query by
prompt similarity, or infer non-creation from the missing external ID. The
separate recovery workflow is pinned to the reviewed master SHA, requires the
authorized human and protected `agent-orchestration` environment, and uses only
the workflow token for read-only identity checks and a compare-and-swap update
of `refs/heads/agent-state-poc-249-v1`. It does not call the Agent API or
perform task, PR, CI, review, label, or status effects. The normal PoC and
bootstrap approvals must both remain `false`.

After this recovery code is reviewed and merged, set
`ORCHESTRATION_POC_DEFINITION_SHA` to that exact merge SHA and temporarily set
`ORCHESTRATION_POC_RECOVERY_APPROVED=true`. Dispatch the recovery workflow from
`master` with Issue/generations `249/1/1`, task
`task:249:1:1:1`, launch intent
`LaunchTask:249:1:task:249:1:1:1:1`, and both acknowledgement inputs set to
`POC-249-ACK-NO-TASK-CREATED`. Supply an audit reason stating that this is the
operator's confirmation, not task-service evidence. The command refuses any
different v1 ref revision, task, intent, actor, definition, or run attempt. If
state changed or the run outcome is uncertain, inspect the v1 ref before doing
anything else; never dispatch recovery again against a changed state.

Verify the recovery artifact and v1 state show the launch marked failed with
the operator attestation recorded, and immediately return
`ORCHESTRATION_POC_RECOVERY_APPROVED` to `false`. The v1 state remains blocked
and immutable to the normal PoC driver. A future attempt uses only a newly
bootstrapped v2 state and a fresh manifest; it requires a separate explicit
authorization and must not be started in the recovery dispatch.

For Issue #237, the canonical intent must be copied from its saved state, not
guessed from a run title. After the new code is reviewed/merged and pinned, the
read-only dashboard must show the expected definition pin. Keep all other
activation and status approvals unchanged; the abandonment command itself is
the separately approved recovery action. Do not run it as part of ordinary
`agent:ready` processing.

The replacement orchestration component is separate from the trading application:
`IntelligentTradingSimulator.Orchestration`. Issue #213's approved reduced-scope
contract uses **label-controlled initiation**, not a mandatory Start button.
Implementation/testing approval is not activation approval. The legacy workflow
remains disabled in GitHub; never restart it for comparison, shadow mode, or rollback.
The replacement jobs are **conditionally gated, not unconditionally disabled**.
The current main-workflow test guard is scoped to
`ORCHESTRATION_TEST_ISSUE=237`, `ORCHESTRATION_RETIRED_TEST_ISSUE=227`, and
`ORCHESTRATION_TEST_MAX_REPAIR_TASKS=1`. Issue #227 is retained historical
state, not an active recovery target. This bounded fixture is additionally
single-pass and does not authorize repair tasks; it is not the final multi-Issue
production configuration.
The reconciler additionally requires its workflow definition to come from
`refs/heads/master`; it does not use the event ref, which is not a branch ref
for Issue events. Managed CI requires the configured `refs/tags/` ref.
Matching configuration can therefore enable these jobs without another code
change. Keep activation explicitly `false` until the separate test approval
and all runtime prerequisites are satisfied.
The reconciler has write permissions for state, projections, CI, and statuses;
its task API step uses a separate protected user credential. Managed CI grants
only `contents: read`. Offline/read-only modes remain the
CLI default; the new `--github-live --execute` path additionally requires explicit
default-false prerequisite attestations. Satisfying the workflow guard alone is
**not** a supported activation procedure. Check live repository/environment
configuration rather than inferring disablement from workflow names or this text.

### Ownership and labels

After a separately reviewed cutover, an authorized human adding `agent:ready` to
an eligible open Issue is a notification to the shared reconciler. The reconciler
must validate numeric repository/Issue/actor identity, live write permission,
open state and current readiness before accepting/queuing it durably. A notification
or queued Actions run is not acknowledgement of durable acceptance.

The canonical phase projects the following labels:

| Phase | Visible status | Next automatic work |
| --- | --- | --- |
| `READY` | `agent:ready` | Wait for repository admission; recheck live readiness |
| `CODING` | `agent:working` | One tracked task; no managed full CI/review |
| `CI_PENDING` | `agent:ci` | CI for the completed task's captured final SHA |
| `REVIEW_PENDING` | `agent:ai-review` | Review the eligible task/round/SHA |
| `FIX_PENDING` | `agent:working` | One recorded repair handoff |
| `HUMAN_REVIEW` | `agent:review` | Human approval; never automatic merge |
| `BLOCKED` | `agent:blocked` | Authorized decision; Resume cannot clear the hold |
| `CLOSED` | No automatic-work label | Preserve terminal record |

At `HUMAN_REVIEW`, ordinary comments are retained as feedback but do not grant
permission to launch work. To send changes back, an authorized human uses
`workflow_dispatch` operation `request-changes` with the exact repository/Issue
generations, handoff ID, round, PR, SHA and a nonempty `reason` describing the
changes. The status projection exposes the handoff ID, not arbitrary hold text. The host
verifies the live open head and completed task's candidate evidence; the reducer
accepts only the current settled accepted-review handoff, never another hold,
pause, single-pass test or uncertain operation. It retains the old handoff,
records the instruction as feedback, and queues `FeedbackRepair` on the same PR.
Pending feedback is included in the repair prompt; existing repair budgets still
apply. The repair must finish and pass fresh CI and review before returning to
human review. Replaying the same command does not create another task. This is
an explicit manual dispatch, not automatic handling of GitHub's "Request changes"
review button. Final approval/merge remains a human action in GitHub.

Duplicate ready notifications do not launch another task. Withdrawal prevents
admission of queued, unstarted work. Withdrawal during active work must stop new
effects without forgetting an active/uncertain task. Stage-label edits only request
projection repair: they cannot manufacture completion, CI success, review acceptance,
or reactivation. Projection-origin notifications do not become commands.
The opt-in label coexists with the stage label until the first task launch is
confirmed. Subsequent stage projections can remove it: durable active authorization
is separate from queue readiness, so projection removal is not withdrawal. An explicit
authorized withdrawal command still stops new effects without losing task identity.

GitHub documents that Issue labels written with `GITHUB_TOKEN` **do not trigger**
another label workflow run. Human actions and suitable PAT/App actions can trigger
one, subject to repository settings. Do not rely on token suppression alone:
the parser also rejects unknown bots and ignores the configured projection actor.
Automated producers using `GITHUB_TOKEN` must invoke the shared reconciler explicitly
or use the exceptional recovery path; they cannot assume adding `agent:ready` wakes
Actions. See [GitHub trigger semantics](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow).

### Persistence, admission and recovery

`GitStateStore` uses an injectable Git object/ref transport. `GitHubGitTransport`
implements the Git database REST shapes without shell pushes and disables mutations
by default. No state branch is created by the CLI or tests. The approved state
reference is `refs/heads/agent-state`. Bootstrap/protection and initial-state review
are explicit operator prerequisites; missing state after setup must fail closed,
not rebuild admission or holds from labels.

Each snapshot contains schema/generation/version, Issue/PR/task/round/SHA identities,
inbox/outbox and repository admission. Publishing a single-parent commit with
`force:false` exposes state plus intents together at one reference. Competing children
cannot both fast-forward: reload and re-reduce on conflict; never merge state or
force-update. Lost publication responses are reconciled using exact commit identity/
ancestry. This is not a transaction with the task, review or CI service.

Record an intent before every effect, and record its executing state before a call.
On restart, reconcile an executing intent with the external system before repeating
it. An unknown result blocks and retains ownership; lease expiry or a failed metadata
write is not permission to launch another task. Only one Issue can hold repository
admission; other ready Issues stay queued. Held/closed/review-ready Issues release it
only when active and uncertain mutations have been resolved. Reacquire it for new work.
Bounded indexes are used instead of scanning all historical PRs.

Reachable Git history has no Actions-artifact expiry, but deletion, force updates
and privileged tampering are not impossible. Protect the reference, monitor size and
stop at capacity; never silently prune pending work or buy storage. Public state
must contain no credentials or private feedback. Backups and GitHub availability
are not strengthened into an unsupported durability SLA.

### Sequencing and integration boundaries

#### GitHub read-only host and projection adapters

`GitHubOrchestrationHost` now composes API-verified ingress, the shared worker/reducer,
and a canonical snapshot read through `GitStateStore`/`GitHubGitTransport`. It checks
repository numeric identity, Actions run event/workflow/actor, current user identity,
collaborator permission, Issue identity and expected workflow generations. The
notification must originate from the trusted Actions event file; checking a run
exists does not authenticate arbitrary user-uploaded JSON as that run's original
payload. Preview/offline modes remain unchanged. The library's write-capable
`ReconcileAsync` composition is separate and requires explicit activation, user-token,
task-API/entitlement and no-added-charge attestations. All default to false; no
ordinary dry-run switch enables them. The explicit production entry point reads
attestations only from the trusted runner environment and verifies the credential
actor through `/user`; fixture files cannot supply production authorization.

The CLI adds an explicitly read-only mode while preserving existing offline fixtures:

```bash
dotnet run --project IntelligentTradingSimulator.Orchestration -- \
  --dry-run --github-read-only \
  --notification "$GITHUB_EVENT_PATH" --event-name "$GITHUB_EVENT_NAME" \
  --repository "$GITHUB_REPOSITORY" --repository-id "$GITHUB_REPOSITORY_ID" \
  --run-id "$GITHUB_RUN_ID" --repository-generation 1 --issue-generation 1 \
  --occurred-at '2026-01-01T00:00:00Z'
```

The timestamp and generations above are examples, not defaults: supply the immutable
notification timestamp and expected canonical generations. `GITHUB_TOKEN` may be
supplied by the host for ordinary REST/Git reads; it is never printed. This mode
performs GETs only, never bootstraps the state branch, never accepts a command durably,
and never launches coding/CI/review or projection writes. Missing canonical state,
missing permissions, API failures or mismatched identities stop the preview.
It has not been run against live user work as part of this implementation.

`GitHubRepositoryApi` reads current Issue readiness/closure and already-mapped PR
repository/head/merge state. It deliberately does not infer a task completion from
a PR, draft state, push, label or CI result. In this base API/preview path,
`CompletedTaskId` remains unset. The separate write-host task observer opens that
gate only after the task-service checks described below.

`ProjectStatus` is a durable outbox effect distinct from `ProjectLabels`; stale
pending projections are cancelled. Status rendering omits arbitrary feedback and
hold text. `GitHubProjectionAdapter` uses documented Issue comment/label REST
endpoints, with mutations disabled by default. Comments are identified by an exact
generation marker **and configured numeric author identity**, not marker text alone.
Unchanged comments are not updated; a changed projection edits the owned comment.
Multiple owned matches stop for reconciliation. Lost creates are lookup-only:
absence is unknown, never authorization to create another comment. Label updates
add/remove only lifecycle labels, preserving unrelated concurrent human labels.
These operations are not atomic with human label edits; live canonical checks remain
authoritative. Collections are fully paginated with a 2,048-item bound and API
responses capped at 2 MiB; incomplete/oversized reads stop rather than guess.

Offline HTTP tests in `GitHubHostIntegrationTests` exercise real REST response shapes,
canonical Git reads, unauthorized/mismatched evidence, missing state, mapped PR
observations without inferred completion, stable projection intents, unchanged/update
behavior, spoofed/duplicate comments, lost create responses, worker-ledger recovery,
retained holds, concurrent unrelated labels and response/pagination limits.
No tests or CLI switches silently enable these adapters in a live worker.

#### Task API and ordinary-feedback implementation

`GitHubTaskAdapter` now implements the documented task POST and repo-scoped task GET.
Launch requires the canonical executing intent, matching task/generation/admission,
an open Issue, configured base branch and expected creator identity. Initial launches
require live `agent:ready` and no PR/head. Mapped tasks verify the same-repository PR
and exact source head before supplying `head_ref`/`base_ref`. The launch prompt is
persisted in the outbox before execution; repair blocker evidence and distinct feedback
event IDs/content are included. Do not pass an installation token to the task client.

Completion requires an explicitly completed remote task, a complete nonempty session
array with completed states, verified task/repository/creator identity, and a
supported GitHub branch or pull artifact mapping to one open PR. The PR's numeric identity,
repository, configured base ref, head ref and SHA are rechecked. The adapter persists
version-1 `bounded-candidate-sha-v1` evidence with the local and remote task IDs,
session IDs, PR number and observed PR head. A trusted `ITaskFinalHeadSource`, when
available, must agree with this snapshot. Otherwise, the snapshot is only a bounded
candidate: **these REST fields do not establish that the task produced the observed
commit**, and a later push could change the same branch. Before CI dispatch/result
acceptance and review-result acceptance, the remote task/session terminal state,
task-artifact-to-PR mapping and exact saved head are revalidated. A mismatch or
ambiguous response cannot authorize the next stage; it remains held or is observed
again. CI dispatch uses only the saved candidate SHA.

Pull artifacts with `data.id` and `data.global_id` now use a bounded, static,
read-only GraphQL `node(id:)` query. `global_id` is an opaque **candidate** node ID:
the task schema names the field but does not specify its semantics. Association
requires a non-null `PullRequest` node, matching `fullDatabaseId` (int64 preserved),
repository name, positive PR number, and matching REST PR database/repository IDs.
The numeric artifact ID is never interpreted as a PR number or decoded into a node ID.
Branch and pull artifacts, when both present, must agree. A narrowly bounded
exception handles an explicitly empty string `global_id`: a branch artifact must
identify exactly one open PR by repository owner, head ref and configured base.
That PR's positive REST database ID must equal the pull artifact's positive Int64
ID, with matching PR number, repository identities, refs and valid head SHA.
Only this empty-string case skips GraphQL; a populated global ID still requires
the GraphQL checks above even when branch evidence is present. Pull-only empty IDs,
missing/null/whitespace/overlong global IDs, duplicate artifacts, ambiguous branch
lookups, partial GraphQL errors, and identity mismatches fail closed. The same
checks apply during initial mapping, completion and candidate revalidation.
No historical PR scan or guessed association is used. This resolves
PR identity, **not** the task's final SHA.

Task failures, waiting/idle/timed-out/cancelled states and unsupported/contradictory
completion responses produce a durable `TaskStopped` hold. Admission and the remote
task identity are retained; Resume cannot clear this uncertainty. The current operator
recovery surface cannot invent a terminal task resolution—verified external resolution
is still required before reactivation. HTTP failures/cancellation propagate without
manufacturing completion. For lost launch responses, lookup can use an already-saved
external ID only; recovery first validates canonical executing/unknown intent,
generation, active task, admission and saved external identity, then verifies the
repository and reads that exact task. Successful lookup confirms launch identity
only, never terminal completion or clearance of a human hold. Absent IDs stay
unknown rather than searching similar prompts or creating another task. The API
provides neither a caller idempotency key nor a unique launch receipt searchable
by request; one visible prompt/ref/time match cannot establish exactly-once identity.
No exactly-once upstream API guarantee is asserted.

`GitHubFeedbackSource` fetches current Issue and mapped-PR comments. REST human author
IDs/logins/types and current collaborator permission are checked; bots and non-writers
are not instructions. Current authorized revisions at/after the generation's durable
`FeedbackSince` boundary become distinct events using repository/generation/target/
comment-ID/update-timestamp identity. Text equality does not deduplicate separate
comments. Accepted revisions survive in the ledger; edits receive new IDs. Original
or deleted revisions that disappeared before observation cannot be recovered.
Permission/API failures fail closed, not silently authorize a commenter.

Owned status-comment projections are excluded using the verified projection actor
and generation marker, including when writes use a human user token.

The write host commits generation-changing reactivation before reading that
generation's feedback. It also fetches comments for newly verified PR mappings
before applying completion. An interrupted invocation can replay its exact committed
reactivation ID/digest against current state without incrementing the generation
again; unaccepted stale commands or changed content cannot use that exception.
It drains validated commands and feedback before task-completion events,
in 128-event pages, remembering each observed immutable event until inbox acceptance.
It feeds the same leased bounded worker and CAS store as existing reducer tests.
Ordinary feedback during coding is queued for a child task; no review may bypass it.
The library host can publish state and perform authorized task/projection effects,
but **CI effects return unknown without a configured, separately approved route**,
producing an explicit hold. The conditionally gated managed route is described below.
Review requests have their own default-false approval gates and
the conservative integration below. This is not a finished coding/CI/review deployment. CLI, replacement
workflow execution requires the temporary test gates and runtime approvals. Scheduled/event continuation wiring is
described below and does not establish a lossless-delivery guarantee.

`GitHubTaskIntegrationTests` uses mocked REST responses for approved/disabled host
invocations, launch deduplication/replay, coding-only sequencing, session/artifact/PR
contradictions, lost response and failed outcome-write recovery, repair prompt content,
distinct/edited/unauthorized feedback and feedback priority. Completion fixtures persist
the candidate record and cover changed task/session state, PR mapping, and head. Reducer
tests cover evidence identity/policy validation and replay idempotency; persistence tests
cover crash recovery from the saved CI intent without re-execution. All approvals in
these tests apply solely to injected HTTP fakes. No live launch or user-work CI was
performed.

Additional mocked GraphQL/REST cases cover pull-only and combined-artifact
resolution, int64 database IDs, absent/contradictory/error responses, current-head
changes, replay, canonical saved-ID recovery, and preserved hold/admission/effect
counts. The real documented task schema still lacks any immutable final commit
field. Session `head_ref`, `base_ref`, `completed_at`, and usage are not SHA evidence;
current PR/ref heads, synthetic `merge_commit_sha`, UI logs and commit-to-session
links are not completion-bound attestations. The approved bounded fallback therefore
records the live PR head only after the task and sessions are observed completed and
the task artifacts uniquely map to that PR; it does not prove who produced that
commit. Maintainers must retain that provenance limitation and independently verify
account access/cost controls before any activation. It must not be described as a
provider-attested task final SHA.

PR association is now observed **independently of completion** through
`ReadMappingEventAsync`/`TaskPrMapped`: confirmed task receipt, repository/creator,
unique branch/pull artifacts and current REST PR identity must agree. The reducer
records the mapping and observed head without changing task status or FinalSha.
Missing artifacts during active coding defer mapping; contradictory artifacts
stop for reconciliation. The host drains newly mapped PR feedback before any
completion/CI effect. `TaskPrMappingTests` covers replay, disabled/unconfirmed
mapping, missing/contradictory identities, and in-progress PR mapping with no CI.

#### Review request and result implementation

`GitHubReviewAdapter` implements the documented Copilot requested-reviewer POST
and bounded reviews/review-comments GETs. `GitHubReviewApproval` defaults to false
for activation, API policy/credentials, no-added-charge verification and disabled
automatic reviews. This approval is separate from task approval. Neither account
access nor cost controls are established by the code or HTTP fixtures.

Before POST, the adapter verifies the canonical executing intent, completed
task/round/final SHA, exact eligible green CI receipt, admission, no holds/feedback,
and live open Issue/PR/head/repository. It verifies the Copilot bot through the
user API and rejects an already requested reviewer or pre-existing pending review.
The request uses the documented `copilot-pull-request-reviewer[bot]` alias.
REST may return that alias or canonical login `Copilot`; either must match numeric
ID `175728472`, node ID `BOT_kgDOCnlnWA`, and type `Bot`. The same pinned identity
is required in acknowledgements, submissions, and comments; login alone is not proof.
The reducer persists a `ReviewRequestBoundary` containing verified bot IDs, time,
and pre-existing review IDs before the non-idempotent call.
Only the first CAS publication may claim an unconfirmed boundary; concurrent
executors cannot re-use even an identical prepared boundary to repeat POST.
Accepted feedback is rechecked after publication and before mutation.
The returned PR must
still match the head/repository and acknowledge the verified bot. A confirmed
boundary is saved before normal outcome publication.

`review-request:<intent-id>` is a **local durable acknowledgement identity**, not
a GitHub review-job ID. The existing `ReviewIdentity.ReviewId` holds this local
receipt for this adapter; actual API review/comment IDs are retained in
`SourceEvidence`. Recovery only uses a saved confirmed boundary to restore that
receipt. A lost response or failed confirmation write remains unknown, even if
the reviewer now appears or a new review exists. It never repeats POST or clears
a human hold. A crash after preparation but before POST also remains uncertain;
operator reconciliation is required rather than guessing that no request occurred.

For new attempts, a missing review approval returns `Failed` with
`FailureCode=ReviewPrerequisitesUnverified` without making API calls.
Read-only preflight rejection returns `ReviewPreflightRejected`, or
`ReviewIdentityMismatch` for an unexpected reviewer identity, before saving a
request boundary or sending POST. These definite refusals still produce a human
hold and are never automatically retried. A concurrent saved request boundary
prevents recording such a refusal as a definite failure; it remains `Unknown`.
After boundary preparation, exceptions conservatively remain `Unknown`.
The shared reconciler records `ExecutionException` or `RecoveryException` for
uncaught execution/recovery failures without storing exception messages,
response bodies, or credentials. Existing `Unknown` receipts are not
reclassified from missing boundaries or new configuration.

Submitted output is fetched only for the current completed/CI-green task and
request. Existing review IDs, other actors, pending and stale-head submissions
cannot satisfy it. Candidate output must match verified bot IDs, PR URL, SHA and
post-boundary submission time. Review comments must match the API review/author/PR
identities. Pagination and total evidence size are bounded; API or malformed
identity failures fail the invocation without promoting or requesting again.

**All captured REST output returns `needs-human`, including `APPROVED`.** GitHub
does not return a request-to-submission round token, so time/actor/SHA alone cannot
prove that output belongs to this request rather than concurrent human/automatic
requests. `COMMENTED` prose is also not reliable blocker/acceptance classification.
Raw review/comment JSON and the uncertainty reason are durably retained with the
typed result and human handoff. No suggestion is silently called a blocker and no
old blocker is cleared. The existing authorized human classification reducer can
then accept/classify/reject the exact handoff and assemble eligible repair prompts.
REST observation plus mocked classification is not production correlation proof.

`GitHubReviewIntegrationTests` covers gated requests, exact task/CI/SHA eligibility,
live identities, intent/boundary/receipt ordering, lost responses and writes,
lookup-only recovery, submitted states and raw evidence, stale/pending/other actors,
malformed scope, paginated-result failures, concurrent executors, replay and
shared-worker feedback precedence. Closed/changed-head live PRs stop review
polling without preventing shared closure/head reconciliation. Full automatic
approval/repair remains blocked pending either a supported trustworthy round and
classification contract or authorized human classification. CLI and workflow
execution require explicit runtime approvals; this library wiring cannot resolve the separate
task-final-SHA blocker. Required-status publication, guarded push routing and
cross-invocation wake-up wiring are implemented below, still default-disabled.

#### Managed exact-SHA CI implementation (conditionally gated)

`GitHubCiAdapter` implements the documented workflow dispatch, run lookup and
paginated jobs APIs. The library host accepts an optional `GitHubCiRoute` and
`GitHubCiApproval`; omission or any false approval prevents dispatch. Activation,
credential/Actions permissions, trusted workflow and no-additional-charge
verification are separate mandatory approvals. CLI still provides no live path.

The route must select `.github/workflows/agent-managed-ci.yml` through an explicit
`refs/tags/` reference pinned to a reviewed definition SHA. Before activation,
the maintainer must create and **immutably protect that tag against updates and
deletion/recreation**, including bypass identities, and verify its workflow and
actions. A prior GET followed by POST is not atomic: the trust approval must cover
this protection, not merely today's hash. Mutable branch refs are rejected.
Workflow/ref reads before dispatch and run evidence afterward validate the pinned
revision; those checks alone cannot prevent executing a changed definition.

The reducer saves a `CiDispatchBoundary` before POST, binding workflow/ref/revision,
task/round/final SHA and a deterministic intent-digest correlation. Only the first
CAS claim may dispatch. Current canonical eligibility, feedback, live Issue/PR/
repository/head and trusted route are rechecked before POST. The [workflow-dispatch
contract](https://docs.github.com/en/rest/actions/workflows#create-a-workflow-dispatch-event)
for API version `2026-03-10` documents HTTP 200 with `workflow_run_id`; the adapter
saves that provider ID before outcome publication. Missing/malformed acknowledgements,
legacy empty responses, lost responses or failed receipt persistence remain unknown.
Lookup-only recovery reads the saved run ID: it never guesses a run by timing/title,
repeats dispatch, clears a hold or substitutes another run. Even a crash after
preparation but before POST requires reconciliation.

**Run/job `head_sha` identifies the trusted workflow definition, not the tested
coding SHA.** The isolated workflow receives the completed task's final SHA as
input, checks out exactly it with `persist-credentials: false`, verifies HEAD,
then runs the repository-required restore/Release build/test commands. It grants
only `contents: read`; it has no state writes, orchestration credentials or secret
inputs. Its only trigger is `workflow_dispatch`. The original CI gate was limited
to test Issue `227`; after the separately reviewed retest-gate change it must
require Issue `237`, cap `1`, and the protected
`refs/tags/agent-managed-ci-test-237-v1` equal to `ORCHESTRATION_CI_REF`.
These are configuration-dependent gates, not a literal-false safety guarantee.

Result correlation checks provider run/workflow/repository/path/event/revision,
intent-derived display title and first attempt, plus the exact run's unique job
name, IDs and successful SHA-verification step. External reruns/changed attempts
stop rather than substitute evidence. Green requires all required steps successful.
A recognized build/test failure after successful restore creates the existing
same-round child repair. Cancellation, restore/verification failure, missing/
duplicate evidence or unknown infrastructure outcomes produce an explicit hold,
not guessed repair. Verified terminal stops retain the run ID/reason separately
from green/red evidence and release admission only when no other operation is
unfinished. Replays cannot clear the hold; explicit human reactivation is required.
Changed/closed live heads cannot satisfy the CI gate, but their saved run is still
polled to record verified terminality without replacement dispatch. Running or
unverifiable runs retain admission for reconciliation.

`GitHubCiIntegrationTests` supplies offline HTTP contracts and shared-worker tests:
default-false gates; task/round/final-SHA, admission and feedback guards; workflow/
ref/live identity checks; boundary-before-POST crashes; concurrent execution; lost
responses/receipt writes; saved-run recovery; replay; successful and repair results;
and cancelled, stale or contradictory evidence. Completed tasks in these fixtures
are **not production completion-SHA evidence**.

Actions still publishes the isolated run on the definition SHA. The separate
trusted required-status publisher below targets the tested PR SHA; branch
protection is unchanged. Final task-SHA evidence and account prerequisites remain
external blockers, not capabilities supplied by these CI fixtures.

#### Durable continuation and production command routing (conditionally gated)

`OrchestrationContinuation` stores one canonical repository-generation ticket
before a leased invocation reads services or executes effects. An unfinished
invocation keeps the ticket and admission/task/round/outbox identities; clean
settlement retires it. A failed cleanup leaves the ticket and lease expiry for
recovery. Verified human commands are committed before unrelated service reads,
so a service failure cannot lose an already-verified ready/pause/reactivation.
Ticket expiry/due time is not task completion or permission to recreate an effect.

The conditionally gated replacement workflow defines periodic `schedule`, exact managed
`workflow_run` completion, PR synchronization/closure and ordinary feedback wakes.
Review results use trusted worker/periodic API polling. Direct `pull_request_review`
wakes are deliberately excluded: their PR merge revision is not the approved
default-branch workflow definition and must not receive orchestration credentials.
`GitHubWakeSource` checks repository/run/workflow/event and the
approved immutable code revision; an upstream CI wake must refer to a saved run.
These wakes only request fresh API observations. Payload heads, status conclusions,
review text, actor labels and comment content never become synthetic completion
or approval events. The leased shared host re-fetches feedback before downstream
effects, performs lookup-only recovery, and automatically continues eligible work.
Duplicate wakes cannot bypass admission or execute the same prepared intent twice.
Scheduled reconciliation also re-observes tracked open PRs/active tasks after
automatic work settles, including HUMAN_REVIEW. A late feedback/head wake that
encounters a lease therefore cannot disappear forever when the active worker
retires its ticket; the next periodic observation still applies status revocation.

The production job checks out only `ORCHESTRATION_DEFINITION_SHA`, never PR code,
and exposes the user credential only to the trusted reconciler step in the
`agent-orchestration` environment. The credential must be environment-only, never
a repository-wide secret accessible to unrelated PR workflows. The user PAT is
used only for Agent tasks and its startup `/user` identity check. The separate
workflow `GITHUB_TOKEN` performs repository observations, review requests,
projections, state writes, CI dispatch and status publication. No downloaded PR
artifacts/caches are executed. Before loading/writing state or executing effects,
the live CLI verifies the PAT's configured human ID and the workflow token's
authenticated GraphQL `viewer` against `github-actions[bot]` / `41898282`, then
cross-checks the bot's REST ID, login, type and node ID. Missing credentials,
identical credentials, API/GraphQL errors or either identity mismatch stop execution.
The bot, not the PAT owner, is the projection actor and status publisher. An
authorized PAT owner's label command therefore works without a second account.
Bot projection labels remain ignored, without requiring the bot to be a collaborator.
The current workflow definition SHA and canonical generations are also verified.
Stable API run timestamps and saved events handle replay; recovery
inputs require explicit expected repository/Issue generations. All library effects,
status publication and the live CLI have default-false activation/policy/cost gates.

[GitHub's event contract](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)
requires the workflow on the default branch; schedules may be delayed/dropped,
and public-repository schedules may be disabled after 60 inactive days. The
15-minute off-hour schedule redelivers canonical pending work, not a guaranteed
timer or lossless event inbox. `workflow_run` chains have a three-level limit;
periodic reconciliation does not depend on indefinite chained dispatches. No
self-dispatch API is used, so a lost dispatch acknowledgement cannot create an
unbounded wake-up storm. Monitor the oldest pending ticket, worker lease expiry,
workflow drift and disabled schedules. Exceptional authorized Resume remains the
recovery path when GitHub cannot deliver wakes; it cannot clear holds.

#### Authorized lookup-only terminal task recovery

`resolve-task` requires the canonical local task ID, reason and expected
generations, an API-verified human with write permission, and a saved external
launch receipt. `ReadTerminalEventAsync` fetches only that task and validates its
creator/repository, complete unique session list, ended states and timestamps.
The host durably records that evidence, then references its exact event ID in the
authorized operator command. Fresh evidence expires after five minutes.
Replayed committed resolution uses its saved command/evidence before another
lookup, including after an interrupted response. No launch or speculative search
is performed.

Resolution marks local tracking **Failed, never Completed**. Receipts, parent/round,
blocker and response obligations, feedback, evidence and human holds survive.
Admission releases only without another unfinished task/CI/review/effect. Explicit
human Reactivate is still required for a new generation. Missing saved task IDs,
active/waiting sessions, incomplete/contradictory evidence or failed lookup remain
blocked. No edit to canonical JSON or absence from a search can resolve them.

The preview API establishes an **ended-session snapshot**, not irreversible remote
termination. Operator resolution acknowledges this limitation; before reactivation,
the maintainer must confirm the remote operation is quiescent and prevent manual
resumption of its old sessions. This is not final-commit evidence and never opens
CI. Missing launch receipts still require an independently verified operation
identity/capability or an explicitly approved design decision; the API has no
caller-intent idempotency/search contract.

#### Explicit review classification

`classify-review` uses the same authorized ingress and live open-head checks.
The operator supplies the exact handoff, PR, round, SHA, reason and explicit
`accepted`/`blocking`/`needs-human` decision, plus bounded JSON arrays of stable
findings and finding-specific responses where needed. The reducer validates
current-round obligations; empty prose, old responses or a manual stage-label edit
cannot clear a blocker. A committed command can replay without reclassifying or
launching another repair. REST output stays human-gated; this command is not an
automated classifier and does not assert provider request-round correlation.

#### Required tested-head status and managed push routing

`GitHubRequiredStatusAdapter` publishes the separate `Agent managed CI` commit
status on the **tested PR SHA**, using the documented REST status create/list
contracts. Its immutable outbox intent binds Issue generation, task, round, head,
CI intent/run and payload. A CAS boundary records verified publisher identity and
pre-existing status IDs before POST; the response is saved before outcome publication.
Read-back checks the exact commit/repository, creator, context, payload, time and
intent-correlated target URL. Lost acknowledgement recovery is lookup-only against
that boundary/receipt; ambiguous or absent evidence never repeats a success create.

Green reflects eligible exact-head CI in `REVIEW_PENDING`, or the same eligible
head after explicit accepted review in `HUMAN_REVIEW`. Before task completion/CI it
is pending; failures, human holds, feedback and stale heads cannot advertise current
success. Review classification and **human final approval remain separate gates**:
a green CI status is never review acceptance or permission for automatic merge.
New pushes update the observed canonical head while retaining the old task-final,
CI and review identities. An untracked completed-head change stops automation;
it does not infer task completion or dispatch replacement CI.

The planner publishes a new guarded status when canonical eligibility changes.
Non-success revocation can execute despite a human hold or admission held by a
different Issue. Status uncertainty remains a human hold for its Issue, not
permission to repeat a create or release an active task. Status APIs are not atomic
with Git state, head pushes or feedback: a late upstream write can race correction.
An unresolvable status create or conflicting writer requires human inspection/
correction of the GitHub context before reactivation; canonical state never promotes
from a remote status. No exactly-once or atomic branch-protection guarantee is claimed.

At cutover the maintainer must require `Agent managed CI` independently of the
ordinary Actions `Build and test` check, retain human review approval, and verify
trusted publisher permissions/provenance. A context name alone does not enforce
the expected REST creator; other permitted writers must not spoof it. These
repository/credential settings remain unmodified and unverified.

The ordinary CI workflow's new read-only route is disabled unless explicitly
approved. It checks out only the reviewed orchestration revision, not PR code,
before reading canonical state with a read-only Actions token. The command verifies
numeric repository/PR identities and independently fetches the live head; labels
and webhook head alone never establish ownership. Verified owned intermediate
pushes and completed-head drift defer ordinary full CI to the managed completion
gate/reconciliation; canonical head lag does not cause intermediate full CI.
Unmanaged PRs, forks and manual/default-branch runs preserve ordinary CI. Missing
canonical state or failed approved routing must stop rather than guess ownership.
Skipped ordinary build jobs are not managed green proof; only the exact saved
managed run can authorize lifecycle progress.

#### Split-credential contracts and permissions

The [Agent tasks authentication guide](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api#authentication)
and [start/get/list REST contracts](https://docs.github.com/en/rest/agent-tasks/agent-tasks)
explicitly support user-to-server tokens, **not installation tokens**. A
repository-selected fine-grained PAT needs **Agent tasks: read and write** for
start (read for get/list), plus implicit Metadata read. It does not need Contents,
Issues, Actions, Commit statuses or Pull requests permissions for this composition.
Task listing is not used to guess the identity of a lost launch.

Conversely, the [Copilot review guide](https://docs.github.com/en/copilot/how-tos/copilot-on-github/use-copilot-agents/copilot-code-review)
directs callers to request `copilot-pull-request-reviewer[bot]` through the
[requested-reviewers endpoint](https://docs.github.com/en/rest/pulls/review-requests#request-reviewers-for-a-pull-request).
The [GitHub App permissions matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-pull-requests)
documents installation-token support with Pull requests write. No documented
Copilot-specific user-token-only exception was found; the PAT is **not** used
for review requests or review observations. Policy, entitlement and billing still
require separate verification; no live request was made to establish them.

[`GITHUB_TOKEN` is a repository-scoped installation access token](https://docs.github.com/en/actions/concepts/security/github_token).
The [installation-authentication guide](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation#using-octokitjs-to-authenticate-with-an-installation-id)
demonstrates authenticated GraphQL `viewer`; REST `/user` is used only for the PAT.
Public `/users/github-actions[bot]` alone would **not** prove credential identity.
The bot ID/login are also documented in [actions/checkout](https://github.com/actions/checkout#push-a-commit-using-the-built-in-token);
this composition targets GitHub.com, not GHES.

The reconciler workflow declares only these permissions (write includes reads;
Metadata read is implicit). The managed CI workflow retains Contents read only.

| Workflow permission | Supported installation-token operations |
| --- | --- |
| `issues: write` | Label add/remove and status-comment create/update; Issue/label/comment/timeline reads. [Issues matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-issues). |
| `contents: write` | Canonical Git blob/tree/commit creation and state-ref fast-forward; commit/ref/compare reads. [Contents matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-contents). |
| `actions: write` | Managed CI `workflow_dispatch`, workflow/run/job observations. [Actions matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-actions). |
| `statuses: write` | Exact-SHA status create/list/combined reads. [Commit-status matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-commit-statuses). |
| `pull-requests: write` | Copilot requested-reviewer mutation and PR/review/comment observations. [Pull-request matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps#repository-permissions-for-pull-requests). |

Repository/collaborator identity reads use implicit Metadata read. No Checks API,
administration, secrets, deployments, workflow-file write or token-minting permission
is requested. `contents: write` is necessary for the Git database API, **not a
GitHub-enforced branch-scoped grant**. It is safe only within this reviewed, isolated
trusted-code composition: checkout credentials are not persisted; untrusted PR code
never runs with these credentials; the transport only builds `state.json` trees,
requires an existing parent, and permits only `refs/heads/agent-state`. It verifies
the expected parent/tip and sends `force: false`; competing children cannot both
fast-forward. The REST ref endpoint has no native expected-old-SHA parameter.
Protect code branches and the state branch against unauthorized writers, force
updates and deletion. This application restriction does not contain arbitrary
compromised workflow code; protect the workflow/definition and environment too.

Bot-generated label/comment events normally do not retrigger Actions under the
`GITHUB_TOKEN` event-suppression rule. Actor filtering still protects replay and
other ingress paths. **`workflow_dispatch` is an explicit exception**, so managed
CI dispatch with `GITHUB_TOKEN` is supported without restoring PAT projections.

#### Solo-maintainer activation guide (not performed)

After the environment secret and canonical state are prepared, run **Agent credentials
preflight (no live effects)** from Actions on `master`, supplying the numeric PAT-owner
ID. It reads the protected environment secret without printing it, verifies the
authenticated PAT and workflow-token identities, and reads the task list and canonical
state. Its only POST is an authenticated GraphQL identity query. It does not start
tasks, request reviews, dispatch CI, publish status, or change labels/state. Passing
this check proves read access only, not task-start permission, entitlement, billing
controls or mutation capabilities. Keep the replacement workflows disabled.

For a separately authorized one-task API smoke test, use **One-time Agent Tasks API
smoke test** on `master` and enter exactly `START-ONE-TASK-224`. It issues one POST
for Issue #224, creates a documentation-only task on `master`, and explicitly does
not request a PR. The task API has no caller idempotency key: if the workflow reports
an uncertain POST result, do not rerun it; reconcile the Agent tasks list first.
The branch may contain one temporary documentation commit, but no PR checks or
review are expected. This proves task-start capability only, not end-to-end state
machine correctness. Legacy remains disabled; replacement execution depends on
the current temporary test configuration and runtime approvals.

The temporary test gate change does not set activation variables or grant runtime
approvals, but it removes unconditional job disablement. Existing matching
configuration can permit execution as soon as that change is available on the
required ref. Legacy disablement remains. The following are operator steps after
separate approval; keep activation `false` throughout preparation.
No second account/seat is needed. A sole maintainer who cannot independently satisfy
entitlement, cost controls or repository protection must leave activation blocked.

1. **Verify eligibility and spending first.** Confirm the PAT owner already has the
   documented Business/Enterprise task entitlement and repository access. Verify
   Copilot review availability/attribution, disable paid overages, and enforce
   stop-usage budgets for AI and Actions (alerts alone are insufficient). Do not
   buy another seat/plan or launch a task/review as a permission probe.
2. **Create the narrow PAT.** In Settings → Developer settings → Personal access
   tokens → Fine-grained tokens, select the existing maintainer as resource owner
   (the repository owner here), select only `IntelligentTradingSimulator`, set an
   expiry, and grant repository **Agent tasks: Read and write**. Metadata read is
   implicit; leave every other selectable permission unset. Obtain any required
   organization approval. With that credential in a private local session, read
   `GET /user` (for example `gh api user --jq '{id,login,type}'` with `GH_TOKEN`
   supplied securely) and record the numeric human ID, never the token, for
   `ORCHESTRATION_ACTOR_ID`. Do not reuse the PAT as `GITHUB_TOKEN`.
3. **Protect the credential environment.** Create repository environment
   `agent-orchestration`; restrict deployment branches/tags to the exact default
   branch `master` only, excluding PR refs and all tags. Add the PAT as the
   **environment secret** `ORCHESTRATION_USER_TOKEN`, not a repository/organization
   secret of that name. Verify unrelated workflows cannot use it from untrusted
   refs. Configure available protection rules without purchasing seats. Per-run
   required-reviewer gates would require repeated clicks; if policy requires them,
   do not promise unattended stages. `GITHUB_TOKEN` is issued by Actions; never
   create a stored secret for it.
4. **Review and bootstrap canonical state exactly once.** Disable admission/effects
   while preparing state. If `agent-state` already exists, inspect its full state
   and outstanding tasks/effects; **do not overwrite or reset it**. For a genuinely
   new installation only, verify `GET /repos/ExarKun1983/IntelligentTradingSimulator/git/ref/heads/agent-state`
   returns a confirmed missing-ref response, not an auth/network error. Using a
   separately authorized operator session with Contents write (not the narrow task
   PAT), the following standard CLI commands create an isolated root tree:

   ```bash
   REPO=ExarKun1983/IntelligentTradingSimulator
   STATE=$(jq -nc --arg repository "$REPO" \
     '{SchemaVersion:1,Generation:1,Version:0,Repository:$repository,Issues:{},Inbox:{},Outbox:{}}')
   BLOB=$(gh api --method POST "repos/$REPO/git/blobs" \
     -f content="$STATE" -f encoding=utf-8 --jq .sha)
   TREE=$(jq -nc --arg blob "$BLOB" \
     '{tree:[{path:"state.json",mode:"100644",type:"blob",sha:$blob}]}' |
     gh api --method POST "repos/$REPO/git/trees" --input - --jq .sha)
   COMMIT=$(gh api --method POST "repos/$REPO/git/commits" \
     -f tree="$TREE" -f message='Bootstrap reviewed agent state' --jq .sha)
   gh api --method POST "repos/$REPO/git/refs" \
     -f ref=refs/heads/agent-state -f sha="$COMMIT"
   ```

   Run each command only after its predecessor succeeds; stop on any error. The
   final create must fail rather than replace an existing ref. Review `state.json`
   and its repository/generation; protect `agent-state` against deletion/force-push
   and unauthorized writes, while permitting the Actions app's state-only
   fast-forwards. Do not require code-PR checks/reviews on this isolated state
   branch that prevent its intended writer. Protect normal code branches separately.
   Never recreate missing state from labels or copy an active installation to a
   fresh generation to escape uncertain effects.
5. **Prepare the reviewed definitions and immutable CI tag.** Keep all workflows
   disabled until the separately reviewed cutover. At that cutover, a human must
   approve a code revision containing the intentionally enabled replacement and
   managed-CI jobs on the default branch; legacy stays disabled. Record that full
   40-character orchestration revision. Create a fresh immutable tag such as
   `agent-managed-ci-v1` at the reviewed CI revision, which must contain the
   approved executable managed-CI job. Set `ORCHESTRATION_CI_REF` to
   `refs/tags/agent-managed-ci-v1`, not a mutable branch. Tag rules must prevent
   updates/deletion/bypass; verify the resolved commit, not only an annotated tag
   object's ID. Never retag to repair drift: review a new tag/revision.
6. **Set every variable below, initially leaving activation/routing false.**
   Put shared values in repository Actions variables (ordinary CI routing also
   reads them); avoid conflicting environment overrides. Obtain numeric IDs with
   an operator's read-only API session:

   ```bash
   gh api repos/ExarKun1983/IntelligentTradingSimulator --jq '{id,default_branch}'
   gh api repos/ExarKun1983/IntelligentTradingSimulator/actions/workflows/agent-state-machine.yml --jq '{id,path}'
   gh api repos/ExarKun1983/IntelligentTradingSimulator/actions/workflows/agent-managed-ci.yml --jq '{id,path}'
   gh api repos/ExarKun1983/IntelligentTradingSimulator/commits/master --jq .sha
   gh api repos/ExarKun1983/IntelligentTradingSimulator/commits/refs/tags/agent-managed-ci-v1 --jq .sha
   ```

   | `vars.*` name | Value and how to obtain/verify it |
   | --- | --- |
   | `ORCHESTRATION_ACTOR_ID` | Numeric PAT-owner ID from authenticated `/user` in step 2; **not** the bot ID. |
   | `ORCHESTRATION_BASE_BRANCH` | `master`, verified from repository `default_branch`. |
   | `ORCHESTRATION_WORKFLOW_ID` | Numeric ID for `agent-state-machine.yml` from workflow GET. |
   | `ORCHESTRATION_DEFINITION_SHA` | Reviewed full commit SHA from step 5; must equal the triggering run's definition/head revision. Update after every merged change to the trusted workflow/code, then verify the read-only dashboard before any live command. A mismatch fails closed; never set it to an unreviewed branch SHA. |
   | `ORCHESTRATION_CI_WORKFLOW_ID` | Numeric ID for `agent-managed-ci.yml` from workflow GET. |
   | `ORCHESTRATION_CI_REF` | Protected `refs/tags/agent-managed-ci-v1` from step 5. |
   | `ORCHESTRATION_CI_DEFINITION_SHA` | Full resolved CI tag commit SHA from step 5. |
   | `ORCHESTRATION_USER_TOKEN_VERIFIED` | Literal `true` only after PAT permissions, owner and repository selection are verified; runtime still authenticates its ID. |
   | `ORCHESTRATION_PROTECTED_CREDENTIAL_ENVIRONMENT_VERIFIED` | `true` only after environment-secret isolation/default-branch policy in step 3 is checked. |
   | `ORCHESTRATION_TASK_ENTITLEMENT_VERIFIED` | `true` only after existing task plan/access is confirmed in step 1. |
   | `ORCHESTRATION_NO_ADDED_CHARGES_VERIFIED` | `true` only after enforced no-overage AI/Actions controls are confirmed in step 1. |
   | `ORCHESTRATION_TRUSTED_DEFINITION_VERIFIED` | `true` only after trusted default-branch workflow/code pin and checkout isolation are reviewed. |
   | `ORCHESTRATION_REVIEW_POLICY_VERIFIED` | `true` only after documented installation-token review permissions and repository Copilot policy/attribution are verified. |
   | `ORCHESTRATION_AUTOMATIC_REVIEWS_DISABLED` | `true` only after automatic Copilot reviews/new-push re-reviews are disabled to prevent duplicate untracked reviews. |
   | `ORCHESTRATION_TRUSTED_CI_VERIFIED` | `true` only after exact-SHA checkout/test steps and immutable executable CI tag are verified. |
   | `ORCHESTRATION_TRUSTED_STATUS_PUBLISHER_VERIFIED` | `true` only after workflow-token bot publisher permissions/provenance are checked; runtime verifies its authenticated ID. |
   | `ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED` | Production: `true` only after branch protection requires `Agent managed CI` and independent human final approval. For this single-pass test, leave unset/false; status publication is disabled and the test PR must remain unmerged. |
   | `ORCHESTRATION_TEST_ISSUE` | The single bounded test Issue. For the existing cleanup operation this remains `227`; only after its verified resolution and the reviewed retest-gate change set this to `237`. |
   | `ORCHESTRATION_RETIRED_TEST_ISSUE` | Leave unset for the #227 cleanup operation. Set to `227` only with retest Issue `237`; runtime verifies the exact durable retired-state evidence. |
   | `ORCHESTRATION_ACTIVATION_APPROVED` | Keep `false`; set `true` **last**, only at separately approved cutover after all prerequisites. |
   | `ORCHESTRATION_MANAGED_ROUTING_APPROVED` | Keep `false` until managed status/protection and unmanaged/manual-CI compatibility are verified; then separately approve `true` for ordinary CI's read-only router. |

   All booleans require the exact lowercase string `true`; missing/other values do
   not attest approval. `GITHUB_REPOSITORY`, `GITHUB_REPOSITORY_ID`, event path/name
   and run ID are supplied by Actions, not `vars.*`. The projection login/ID are
   fixed and authenticated at startup, not operator-supplied variables.

   The live CLI preflights review and managed-CI approvals before API calls,
   state writes, or coding, including in bounded test mode. This requires
   `ORCHESTRATION_REVIEW_POLICY_VERIFIED`,
   `ORCHESTRATION_AUTOMATIC_REVIEWS_DISABLED`, and
   `ORCHESTRATION_TRUSTED_CI_VERIFIED` as well as task/credential/cost gates.
   The read-only gate diagnostic reports these downstream comparisons too.
   Do not set an attestation merely to pass a gate; verify the underlying policy.
   Explicit `pause` and `resolve-task` recovery commands retain their existing
   task, activation, credential, and authorization checks, but do not require
   downstream review/CI approvals. The CLI runs those commands in command-only
   mode: it saves the verified command/evidence without running the worker or
   admitting/launching queued work. For a held mapped head, the only permitted
   effect is an `error` publication for `Agent managed CI`, through the existing
   authenticated status publisher and all status-publication approval gates.
   This publishes a correction for an earlier green status even when review/CI
   approvals are missing.
   It does not read task/CI/review services (except terminal evidence required by
   `resolve-task`), plan label/comment projections, or publish `success`.
   A prepared or uncertain revocation is lookup-only on command replay; a lost
   response never permits another POST for that intent. Unconfirmed delivery
   returns `Blocked` and preserves the hold and durable status intent. At host
   level, if status history exists but publisher approvals are unavailable, the
   command is saved but the invocation fails explicitly rather than claiming
   revocation; CLI startup gates may reject unverified configuration earlier.
   Bounded tests with no status history and status publication disabled remain
   command-only without a status write. Commit statuses are not transactional:
   a late uncertain earlier success can still overwrite the corrective error;
   preserve the hold and independently inspect the latest exact-SHA/context
   status before any separately authorized reactivation.
   `resume`, `reactivate`, classification,
   readiness, and automatic wakes still require full pipeline preflight.
   This change does not recover the held #237 attempt or authorize another
   review, move its immutable CI tag, or change deployed definition pins.

   The proposed separate coding/CI/status/pause verification is specified in
   [the isolated status-test design](orchestration-isolated-status-test.md).
   The isolated profile is implemented in the open PR but is not yet merged,
   provisioned, or authorized to run. Do not repurpose the existing #237
   workflows or state to approximate that test.

   For the explicitly approved bounded end-to-end test only, set
   `ORCHESTRATION_TEST_ISSUE` (the sole active test Issue) and
   `ORCHESTRATION_TEST_MAX_REPAIR_TASKS=1`. The #237 retest additionally requires
   `ORCHESTRATION_RETIRED_TEST_ISSUE=227`, and only after the prior task's audited
   resolution. The live host then rejects commands for
   other Issues and validates every canonical state/effect snapshot loaded during
   dispatch, recovery, and wake processing; it also requires the test Issue's
   persisted repair cap and single-pass flag to match. Both are persisted with
   the authorized Ready event. This temporary single-pass test permits one coding
   task, at most one managed CI intent, and at most one review intent after green
   CI. A failing CI stops immediately; feedback or review findings requiring repair
   also stop instead of creating any repair task. Reactivation is rejected.
   The single-pass limits survive replay, Resume, and removal of test variables.
   If `ORCHESTRATION_REQUIRED_CONTEXT_VERIFIED` is unset/false in this bounded
   test mode, required-status publication is disabled. This is acceptable only
   because the test PR must not be merged; production still requires the verified
   protected status context before activation.
   The cap variable remains `1` for configuration compatibility, but single-pass
   mode is stricter and permits no repairs. Remove both variables immediately
   after the test and leave activation false.
   If either variable is missing or the cap is not exactly one, bounded test mode
   fails closed. Ordinary Issues without the persisted single-pass flag retain
   existing repair budgets.
   The initial temporary workflow job gates permit only test Issue `227`, cap
   `1`, and activation `true`. After the stopped #227 attempt is resolved, a
   separately reviewed retest definition may target only Issue `237` with cap
   `1` and `ORCHESTRATION_RETIRED_TEST_ISSUE=227`. Do not merge that gate change
   before #227's audited `resolve-task` completes; doing so blocks the old
   resolution command.

   For #237, the canonical `agent-state` intentionally retains the historical
   #227 record. Bounded-state validation allows that one prior Issue only when
   its exact single-pass task/launch identity matches, the Issue, task, and launch
   rounds are all `1`, the task is locally Failed with durable ended-session
   and operator-resolution events, the Issue
   remains BLOCKED with `ReactivationRequired`, admission is not held by #227,
   and all of its effects are terminal. Both saved event receipt digests must
   match their terminal and operator-resolution payloads. Missing, changed,
   active, uncertain or additional historical work fails closed. The #237 bounded scope cannot
   reactivate or act on #227. It validates #227's canonical history but excludes
   it from live task, issue, feedback, CI, and review reads and from projection
   planning during #237 reconciliation, so historical label drift or absent projection
   receipts cannot block the active retest.

   The new managed-CI ref is `refs/tags/agent-managed-ci-test-237-v1`. Before
   creating it, configure an organization/repository tag ruleset that prevents
   update and deletion for this exact tag pattern. Create a lightweight tag
   only after the gate/workflow commit has been reviewed and merged, pointing
   directly at that immutable commit. Set `ORCHESTRATION_CI_REF` to this ref
   and `ORCHESTRATION_CI_DEFINITION_SHA` to its exact 40-character commit
   target. Never move or reuse `agent-managed-ci-test-227-v2`. The manual
   read-only gate diagnostic checks the new tag target against the configured
   CI SHA; independently verify the tag ruleset and target before activation.
   Existing tag rulesets for the #227 v1/v2 tags are exact-ref rules and do not
   protect this new #237 tag.

   The reconciler must run from `master`, and its definition SHA must match the
   reviewed merge commit. Removing test variables disables these jobs, rather
   than activating production. These bounded-scope variables alone are not a
   complete live configuration: all prerequisites, approvals, and definition
   pins listed above remain mandatory.
7. **Approve cutover, then command work as yourself.** After the separate guard
   change/review, protected environment/state/tag, all attestations, and explicit
   approval, enable activation last. Add `agent:ready` to an open Issue as the
   same PAT-owning human with write/maintain/admin permission. No normal Start
   button or second identity is required. Watch canonical admission/queue and the
   single bot-owned status comment. Keep the bounded candidate-SHA policy:
   completed terminal tasks/sessions plus observed task↔PR/head association are
   revalidated, **not** provider attestation of a task-produced commit. Ambiguous
   reviews require exact handoff classification, not guessed prose. Monitor old
   tickets, lease expiry and delayed/dropped/disabled schedules; guarded Resume
   is exceptional recovery, not a lifecycle override.
8. **Rollback without forgetting operations.** Set activation and managed routing
   false, disable the replacement and managed-CI workflows, and restore both
   literal-false guards in a reviewed change; never enable legacy orchestration.
   Cancel in-flight reconciler runs and remove/revoke the environment PAT if
   isolation is in doubt (changing a variable does not retract a running token).
   Inspect already-dispatched tasks/reviews/CI independently: cancellation or PAT
   revocation does not prove remote termination. Preserve `agent-state`, receipts,
   generations, leases, uncertain effects and human holds; never reset/force-push
   state to retry. Check the exact-SHA required status for stale success and require
   authorized correction/human review before any merge. Ordinary CI resumes with
   routing disabled. Reconcile saved remote identities lookup-only, verify
   quiescence, then obtain fresh approval before reactivation.

#### Verified API contracts versus activation prerequisites

Read-only documentation research on 2026-11-03/04 establishes public contracts, **not
this account's access, credentials, entitlements, policy or spending configuration**:

| Surface | Documented contract | Remaining work / prerequisite |
| --- | --- | --- |
| Coding tasks | [`POST /agents/repos/{owner}/{repo}/tasks`, repo/task GETs](https://docs.github.com/en/rest/agent-tasks/agent-tasks). Public preview; launch requires Business/Enterprise. `prompt`, `base_ref`, `head_ref`, `create_pull_request` are documented and implemented with offline HTTP coverage. | Verify eligible existing plan and a user access token with Agent tasks permissions. Installation tokens, including `GITHUB_TOKEN`, are unsupported. No plan upgrade is authorized. Lost creates without a unique saved ID remain blocked. |
| Task/PR association | Branch lookup and pull-artifact candidate global-node lookup with GraphQL/REST database/repository checks are implemented. After a completed task and all its sessions are verified terminal, the bounded candidate-SHA policy persists a versioned record of task/session IDs, the mapped PR, and its observed head. [Global node guide](https://docs.github.com/en/graphql/guides/using-global-node-ids), [PR fields](https://docs.github.com/en/graphql/reference/pulls#object-pullrequest). | This snapshot is not provider attestation: the task API does not prove that the observed head was produced by that task. Revalidate task/session state, task↔PR mapping, and exact head before CI dispatch/result and review-result acceptance; any mismatch holds or requires a fresh observation. Pull artifacts lacking usable global IDs also hold; no PR-number guessing. |
| Follow-up coding | A new task can target an existing `head_ref`/`base_ref`; [PR mentions](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-on-github) are a separate interaction. | Preserve child task/round/blocker identity. No supported append-to-existing-task endpoint or bot-mention task-ID response is established. |
| Review request | [Requested reviewers REST](https://docs.github.com/en/rest/pulls/review-requests#request-reviewers-for-a-pull-request) and [Copilot review guide](https://docs.github.com/en/copilot/how-tos/copilot-on-github/use-copilot-agents/copilot-code-review) document `copilot-pull-request-reviewer[bot]`. Gated request/boundary/receipt and lookup-only recovery are implemented. | Response is a PR, not a review-job ID. Verify repository policy/auth, automatic-review settings and account billing. Missing confirmed receipts remain uncertain; no speculative retry. |
| Review result | [PR reviews/comments](https://docs.github.com/en/rest/pulls/reviews) expose actor, `commit_id`, review ID and comments; bounded validated raw capture is implemented. | No request-round token or dependable prose classifier; captured output is `needs-human`, never guessed promotion/repair. Authorized classification or independently verified provider evidence is required. |
| Lost mutation response | Neither task launch nor review request documents a caller-supplied idempotency key or exactly-once creation. | Persist intent before call; retain returned identity; if no unique operation can be established, hold for human reconciliation, never repeat a speculative create. |
| CI/status | [Workflow dispatch](https://docs.github.com/en/rest/actions/workflows#create-a-workflow-dispatch-event), [runs](https://docs.github.com/en/rest/actions/workflow-runs), [jobs](https://docs.github.com/en/rest/actions/workflow-jobs) and [commit statuses](https://docs.github.com/en/rest/commits/statuses) contracts; gated adapters, exact persisted candidate-SHA testing/status, conditionally gated exact-SHA workflow and default-disabled managed push routing. | Verify credential/publisher/context policy, immutable trusted tag/revision and no-added-charge enforcement. The bounded candidate SHA is only the terminal-task-time observed PR head, not an authoritative task-produced commit. Workflow execution depends on configuration; branch protection must be checked independently. |
| Continuation | Documented default-branch schedule/workflow-run/comment/PR wakes; durable canonical ticket and leased shared host, conditionally gated workflow wiring. | GitHub may drop/delay schedules or disable inactive public schedules; monitor and use exceptional guarded Resume. Trusted definition/default-branch placement and account cost controls require maintainer verification. No lossless SLA. |
| Operator recovery | Saved task GET/session snapshot plus authorized `resolve-task`; local tracking ends Failed, with durable evidence and replay. `classify-review` references exact current handoff/head/round. | Task API does not promise irreversible remote termination. Verify quiescence before Reactivate; missing saved launch ID remains unresolved without an authoritative operation identity/design decision. No completion-SHA inference. |
| Charges | [Organization billing](https://docs.github.com/en/copilot/concepts/billing-and-usage/organizations-and-enterprises/billing), [budgets](https://docs.github.com/en/copilot/concepts/billing-and-usage/organizations-and-enterprises/budgets), and [review pricing](https://docs.github.com/en/copilot/reference/copilot-billing/models-and-pricing) describe paid usage and review attribution. | Maintainer/admin must verify existing entitlement, attribution, disable paid overages, and enforce stop-usage controls for AI and Actions. Alerts alone are not caps; public Actions compute does not establish free AI use. No per-operation included-only billing field was established. |

#### #213 acceptance evidence and remaining gaps

This is a tracking matrix, **not a declaration that all acceptance criteria pass**.
The first column follows the Issue's acceptance-list order. Test file names below
refer to `IntelligentTradingSimulator.Tests/Orchestration`.

| # / requirement | Implemented evidence | Outstanding / blocked |
| --- | --- | --- |
| 1 Legal/illegal transitions and invariants | `OrchestrationCoreTests`; terminal-evidence/operator and required-status reducer guards | Offline proof; exhaustive possible upstream failures not claimed. |
| 2 Actual handlers/replay/reorder/concurrency | Ingress/Host/task/review/CI HTTP suites; `SharedHostLifecycleIntegrationTests`, status/routing/continuation, independent task-PR mapping and operator Host tests | Live account/contracts and activation remain unverified; transient unobserved events are not lossless. |
| 3 Intent/effect crash boundaries | Persistence/worker CAS; task/review/CI/status boundary/POST/receipt/outcome failures; ticket-before-work and stable operator evidence replay | No exactly-once upstream guarantee; lost task/review IDs remain human-blocked. |
| 4 Exact counts/preserved holds | Single-create/concurrent/recovery tests; duplicate wakes keep one launch; operator replay never looks up/relaunches committed resolution | Unknown projection writes require context inspection; no speculative retries. |
| 5 Coding before exact-SHA CI | Completed task/session and unique PR mapping; versioned bounded candidate-SHA evidence; revalidation at CI dispatch/result and review-result acceptance; exact persisted-SHA checkout/verification; offline head/task/mapping/replay/crash tests | The preview task API cannot attest that the observed candidate commit was produced by the task. Mismatches fail closed or require re-observation; this fallback does not establish final-commit provenance. |
| 6 Green/review and two repair cycles | Worker multi-repair loops; actual Host task→CI→review→classification→status and repair coverage | Autonomous review correlation/classification unavailable; explicit human classification supported. |
| 7 Stale heads/checks/unmanaged compatibility | Exact tested-head status and run/step tests; head/feedback revocation; `ManagedPushRoutingTests`; default-disabled trusted CI router | Publisher/required-context/human-approval policy must be verified at cutover; no settings changed. |
| 8 Feedback priority/distinct identical events | Paged worker, authorized distinct/edit feedback tests; HumanReview feedback ingestion and current-status revocation | No recovery of revisions deleted before observation; API/capture uncertainty escalates. |
| 9 Productive rounds/no-progress stop | Finding-specific/reintroduced-blocker core tests; Host classification cannot erase unresolved evidence | Provider request-round proof unavailable; humans classify exact handoff, never guessed prose. |
| 10 Closure/reopen/reactivation | Core/REST closure checks; verified ended-session/operator evidence with release/no-release/replay tests; human holds remain | Snapshot is not irreversible task termination; missing launch ID requires external identity/design decision. |
| 11 Operations documentation | This runbook: trust/cost/cutover/rollback, continuation monitoring, status provenance and operator procedures | Maintainer must verify account/prerequisites and approve activation separately. |
| 12 Human final approval/no auto-merge | Core `HUMAN_REVIEW`; Host classification leaves final approval hold; no merge effect/API | CI status alone is not approval; preserve independent human branch protection. |
| 13 Label start/queue/guarded recovery | Verified command committed before service reads; split-credential PAT-owner/bot ingress, identity and replay tests; live CLI and conditionally gated workflow wiring | Solo maintainer uses the existing human PAT only for task APIs; Actions bot owns projections/state. Account/prerequisites remain unverified. No normal Start button. |
| 14 Automatic stages/labels/continuation | Leased host, durable ticket, scheduled/event wake verification, bounded automatic polls and status projections | Configuration-dependent test gates and runtime approvals; GitHub schedule delays/drop/inactivity require monitoring and exceptional Resume, not a delivery SLA. |
| 15 Shared C#/real response shapes | One reducer/reconciler; REST task/session/GraphQL/review/CI/status fixtures, trusted routing/wake adapters and production composition | Public contracts/offline fixtures do not establish this account's eligibility, credentials, spending or final-SHA capability. |
| 16 Repository admission/queue | Core/CAS/worker concurrent admission; duplicate/expired-lease continuation tests; task recovery retains other unfinished work | Requires protected canonical branch; no paid database/external worker or premature lease release. |
| 17 Reviewer needs-human | Raw output/handoff capture, explicit authorized live-head classification, unresolved finding regression and status revocation | Autonomous request-round/classification evidence absent; human gating remains intentional. |
| 18 Comment replay/recovery state | Owned status-comment HTTP tests and lost-create lookup recovery; full host event/status/feedback replay | External tamper/deletion cannot be guaranteed impossible; monitoring and uncertainty holds remain. |

Separate activation approval, canonical branch bootstrap/protection, credential/policy
verification, and demonstrated zero-additional-charge enforcement remain mandatory.
Neither the legacy workflow nor replacement live execution is enabled by this work.

Consolidated offline validation for this pass: solution restore and Release build
passed; full suite **1,963 passed, one existing SQL Server skip**; orchestration
selection **553 passed**; unchanged legacy harness **90 passed, zero failed**.
Focused cases include the actual hosted lifecycle, task PR mapping before completion,
status receipt loss/replay, current-head/feedback revocation, contended late feedback
followed by a scheduled wake, and terminal operator evidence interruption/recovery.
All API/effect fixtures remain offline. These results do not verify production
completion-SHA capability, account billing/access, credential protection or activation.

An initial coding task may use the explicit `UnmappedIssue` launch source only when
both PR and head are absent and no parent task exists. Mapped coding and all repair
tasks use `PullRequestHead` and require the exact valid source SHA. Launch confirmation
persists the external task identity; completion records its versioned candidate PR-head
evidence. Live PR, candidate head and completed-task identity must all match before CI
is created.
Duplicate readiness notifications or Resume do not create another task.

Only explicit completion of the current confirmed task plus a matching live head
permits CI. Store the completed task, response round, candidate SHA evidence, CI intent
and run.
Intermediate pushes, early/unrelated green CI, labels and Resume cannot open this
gate. Eligible green CI alone can authorize the current review request. A failed
build/test creates a tracked child coding repair on the same PR; complete it before
CI again, preserving blocker/round obligations. Technical CI failures have a
consecutive budget of three; only eligible green CI resets it. Productive review
cycles have no arbitrary round cap. Changed SHA/green CI are not per-finding progress.
Current findings are unresolved obligations. Removing one requires fresh finding-specific
evidence in the eligible review/classification event; historical responses are audit
only, not clearance for a later occurrence of the same blocker. Replaying a historical
response neither clears a reopened finding nor counts as progress. `NeedsHuman` retains
unresolved findings under the same rule, and human acceptance cannot bypass it.

Offline/read-only entry points do not enable live integration. The separately gated
production entry point wires trusted observations, authorization, Git credentials
and supported task/CI/review/status adapters. The test executor is not a production API.
In particular:

- Verify Agent Tasks endpoint availability, user-to-server authentication,
  numeric repository/artifact identities and explicit `completed` states using real
  response shapes. Do not assume installation tokens work or creation is idempotent.
- Verify reviewer semantics. Prose, badges, missing findings and `COMMENTED` reviews
  are not approval/blocker contracts. Unsupported output is `needs-human`.
  An authorized, round/head-scoped classification decision must be recorded before
  an automatic handoff can resume. Do not add a separately billed classifier.
- Verify exact-SHA CI at cutover: trusted workflow definition, checkout of captured
  SHA with `HEAD` verification, durable dispatch/run correlation, checks against that
  tested head, and no credentials in PR-code test jobs. Gate managed intermediate
  push CI without changing unrelated/manual PR behavior. The new route remains
  disabled until its dedicated status and trusted code identity are verified.
- Verify bounded task/run observations and supported wakes through the same worker.
  Persist before yielding. Manual **Resume** is exceptional recovery, not normal Start
  or a per-stage action. It cannot reset rounds/retry history, clear a hold or duplicate
  pending effects. Pause/reactivation are distinct authorized decisions with reasons.
- Ordinary feedback can be fetched, but Actions is not a lossless inbox for every
  edited/deleted/transient event. Only durably accepted explicit commands have that
  recovery contract. Ambiguous history stops; do not infer missing approval.
- Status-comment identity/rendered-content reconciliation must be verified before
  enabling projection writes; labels/comments never become the audit database.

### Bounded worker and exceptional Resume

`OrchestrationWorker` hosts the shared reducer/reconciler; it does not add a second
decision path or enable the CLI's live effects. Non-dry-run invocations first claim
a repository-wide worker lease with a CAS state publication. Its expiry is the
invocation's maximum duration plus a one-minute recovery margin. Concurrent Resume
returns `LeaseHeld` without inspecting or executing another owner's operation.
Normal exit/cancellation releases only that invocation's lease using a separately
bounded cleanup; failed cleanup leaves the recorded expiry intact. Wait for that
expiry before recovery after an unrecoverable worker/storage crash. Expiry never
releases repository admission or authorizes repeating an executing operation.
Production hosts must use this worker for automatic execution rather than calling
the single-step reconciler concurrently outside its lease.

Its injectable event source returns
validated, stable-ID events. Before processing notifications, the worker performs
lookup-only recovery of any executing/uncertain intent so a completion notification
can refer to the restored task/run/review identity. This step cannot launch an
operation or clear a hold. Unconfirmed service results remain unacknowledged while
identity recovery is ambiguous; explicit human commands can still be recorded.
The worker commits each eligible event through `DispatchAsync` before observing
fresh lifecycle guards and processing one recorded effect. A
partial batch failure leaves earlier committed events recoverable; the source must
redeliver uncommitted events with their original IDs, payloads and timestamps.
This is not a guarantee of lossless capture before the source recorded an event.
Each batch reports `HasMore`. While more input pages remain, the worker commits
events but does not authorize new effects, so later feedback/hold commands take
priority. Dry-run reports `EventBacklog` without proposing effects for partial input
or service results still awaiting identity confirmation.

The default bounds are 128 iterations, 128 events per batch and 30 minutes per
invocation. A caller may lower the duration, but cannot exceed 30 minutes. Progress
drains immediately; unchanged state polls at 30-second intervals. Unchanged polls
do not write state or launch replacement tasks. Invalid/oversized batches fail
closed rather than silently dropping events. A host should page by stable pending
identities within that bound, not scan all historical PRs.

An iteration/time limit yields without clearing admission, task/round identity,
retry history, inbox records, or outbox intents. Deadline cancellation during an
external call leaves the intent executing: the next invocation performs lookup-only
reconciliation before any new call. Explicit caller cancellation propagates so a
cancelled Actions job cannot report successful completion. The returned
`LastReconciliation` is the last completed reconciliation, not proof of the current
state after cancellation; recovery always reloads canonical storage.

The worker returns immediately in dry-run/effects-disabled mode. Dry-run applies
events and decisions in memory, without accepting commands or executing effects.
An ambiguous external outcome stops the invocation as `Blocked`; Resume can look
up that recorded operation but cannot clear its human hold. Once no authorized
automatic work remains, the worker reports `Settled` (or `Blocked` when a hold
remains); neither means that a PR was merged or that live activation was approved.
Offline tests exercise the worker with fake APIs, including concurrent invocations,
two CI repairs, review repair/classification, queued admission, and cancellation
after an external call. Production sources/adapters, wake-up wiring, status-comment
projection and exact-SHA CI routing remain cutover prerequisites, not capabilities
enabled by this host loop.

### Offline replay and validation

The executable supports only `--dry-run`; there is no live execution switch.
A replay fixture is local JSON with `State`, `Events`, `Observation` and
`AuthorizedActors`. Events have a `Data.type` discriminator (`ready`, `withdraw`,
`stage-edited`, `resume`, `pause`, `reactivate`, `task-completed`, `head-changed`,
`ci-result`, `review-result`, `human-feedback`, `human-classification`, `closure`,
`reopen`). Fixture actors
authorize only offline simulation, never live requests.

For example, save this empty replay to a file under `/tmp`:

```json
{
  "State": { "SchemaVersion": 1, "Generation": 1, "Repository": "owner/repo" },
  "Events": [],
  "Observation": { "Issues": {} },
  "AuthorizedActors": []
}
```

From the repository root:

```bash
dotnet run --project IntelligentTradingSimulator.Orchestration --configuration Release -- \
  --dry-run --replay /tmp/orchestration-replay.json
dotnet test IntelligentTradingSimulator.Tests --configuration Release \
  --filter FullyQualifiedName~Orchestration
```

Output explicitly reports `AcceptedDurably: false` and `ExternalEffectsExecuted: 0`.
`--notification <payload> --event-name issues --dry-run` inspects notification context;
it does not trust payload actors as authorization or accept work. Adding
`--context /tmp/notification-context.json` exercises validated notification conversion
and the **same reducer** used by reconciliation. That offline context contains
`State`, `Observation`, `Evidence` (an `IngressEvidence` object), `RunId`,
`RepositoryGeneration`, `IssueGeneration` and `OccurredAt`. Numeric actor identity,
permissions, live readiness and workflow generations are validated before producing
typed events; stage edits only dirty their projection. All evidence in a fixture is
synthetic, not proof of production API permissions. Reuse the original run ID and
timestamp when replaying a notification. Reactivation includes its explicit `Reason`.
Invalid input fails
without echoing arbitrary payloads into diagnostics. Tests use fake APIs/transports,
including Git publication conflicts and lost responses; no test launches a live agent,
CI run, review or remote state write.

### Cost and activation checklist

The public repository's standard hosted Actions compute is currently free, but AI
credits, artifacts, caches, LFS, larger runners and a change to private visibility
must not be assumed free. Current documented allowances/limits include Free/Pro
artifact storage of 500 MB/1 GB (shared with Packages), cache allowance 10 GB/repository,
20/40 standard concurrent jobs, and `GITHUB_TOKEN` REST limit 1,000 requests/hour/
repository. Artifacts/logs normally expire after 90 days; schedules and pending
Actions runs can be dropped. None is canonical storage.

Illustratively, 20 Issues/month × two 30-minute worker invocations is 1,200 standard
runner-minutes before CI, not an AI usage forecast or guaranteed account allowance.
This implementation adds no external service, package dependency, cache upload,
artifact upload or paid runner. Actual account AI entitlement, remaining allowance,
effective no-overage enforcement and reviewer/API capabilities remain **unverified**.
No live effects may be enabled on that basis.

Before separate activation approval:

1. Reconcile residual legacy tasks/PR mappings and uncertain work; leave legacy disabled.
2. Verify/protect/bootstrap canonical state and approve its public-data/retention policy.
3. Verify current API shapes, identities, permissions and reliable review classification.
4. Verify existing entitlement and provider-enforced no-additional-charge controls.
   A local counter or billing alert alone is not a hard cap on variable-cost sessions.
5. Complete and review live host, exact-SHA CI/check routing and projections described
   above, using offline crash/replay tests first. No user-work dispatch is authorized.
6. Obtain explicit maintainer approval for the cutover. Rollback pauses effects and
   reconciles retained canonical state; never restarts the known-broken legacy workflow.

Sources: [Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions),
[Actions limits](https://docs.github.com/en/actions/reference/limits),
[Git references](https://docs.github.com/en/rest/git/refs),
[Agent Tasks](https://docs.github.com/en/rest/agent-tasks/agent-tasks).
