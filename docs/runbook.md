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
| `Use52WeekHigh` | Enable distance-from-52-week-high scoring. |
| `Week52HighScoreWeight` | Non-negative maximum score contribution for the 52-week-high factor. |
| `UseQualityFilter` | Enable the quality filter. |

The loader validates strategy settings at startup. Missing settings use the defaults in `StrategyConfiguration`; invalid or missing active strategy sections stop startup with a configuration error.

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
Max drawdown sorts lowest first; all other metrics sort highest first.
Trade-count ranking measures activity, not profitability.
For alpha or relative return, also specify `--comparison-benchmark=SPY` (or another
configured benchmark symbol). Only that benchmark's existing metrics are used.

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
