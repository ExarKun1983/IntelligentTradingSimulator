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
| `LookbackDays` | Historical lookback used by analysis; must be greater than zero. |
| `UseRsi` | Enable RSI scoring. |
| `RsiPreferredMinimum`, `RsiPreferredMaximum` | Preferred RSI range, each from 0 to 100; minimum cannot exceed maximum. |
| `RsiOverboughtThreshold` | RSI penalty threshold, from 0 to 100 and not below the preferred maximum. |
| `RsiPreferredRangeBonus`, `RsiOverboughtPenalty` | Non-negative score bonus and penalty. |
| `UseRelativeStrength` | Enable relative-strength analysis. |
| `RelativeStrengthBenchmarkSymbol` | Benchmark ticker for Relative Strength (default `SPY`; for example `QQQ` or `IWM`). Must not be blank. |
| `Use52WeekHigh` | Enable distance-from-52-week-high scoring. |
| `Week52HighScoreWeight` | Non-negative maximum score contribution for the 52-week-high factor. |
| `UseQualityFilter` | Enable the quality filter. |

The loader validates strategy settings at startup. Missing settings use the defaults in `StrategyConfiguration`; invalid or missing active strategy sections stop startup with a configuration error.

Relative Strength is the stock's close-to-close return minus the selected benchmark's return, in percentage points (not a price ratio or RSI). Positive values indicate outperformance; negative values indicate underperformance. Analysis uses the stock's first and last available sessions in the requested lookback, with benchmark closes required on those exact dates. No candles after the analysis day are used, including during backtests. Missing or ambiguous endpoints, fewer than two distinct sessions, non-positive endpoint prices, or arithmetic overflow produce `null` (`N/A` in market ranking details); benchmark download failures do not prevent stock analysis. Interior missing sessions do not affect close-to-close returns. `UseRelativeStrength=false` skips benchmark fetching and leaves the value unavailable.

The value is exposed as `ObservationResult.RelativeStrengthPercent` alongside `RelativeStrengthBenchmarkSymbol`, including in `BacktestDayResult.Observations`. It does not change scores or ranking weights (#30). Like RSI and 52-week-high details, these analysis-only fields are not saved in the existing observation-history table.

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

There is no dedicated structured export command or CSV/JSON exporter in the current application. Console output can be captured as text from a Bash-compatible shell:

```bash
dotnet run --project IntelligentTradingSimulator.App -- \
  --run-backtest \
  --backtest-start=2025-01-01 \
  --backtest-end=2025-03-31 \
  --backtest-name=Example | tee backtest-output.txt
```

This captures human-readable console output, not a machine-readable export.

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
