# Local development runbook

This guide covers the current console application and its SQL Server and Yahoo Finance dependencies. Backtests and the regular simulation retrieve market data from Yahoo Finance, so those runs require an internet connection.

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

`FinancialModelingPrepFundamentalDataProvider` implements `IFundamentalDataProvider` to fetch fundamental metrics directly from FinancialModelingPrep (FMP) APIs (`financial-growth` and `ratios` endpoints).

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

Ranking and backtesting use the same Core `CompositeScoringEngine` via `IScoringEngine`. It accepts the already-calculated metrics in `ScoringContext` and adds contributions in a fixed order using decimal arithmetic:

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
