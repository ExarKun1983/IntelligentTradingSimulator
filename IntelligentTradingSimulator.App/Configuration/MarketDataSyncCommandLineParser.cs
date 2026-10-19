using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace IntelligentTradingSimulator.App.Configuration;

public static class MarketDataSyncCommandLineParser
{
    public static bool TryParse(
        string[] args, out bool synchronize, out DateTime from, out DateTime to,
        out IReadOnlyList<string> symbols, out string? error)
    {
        synchronize = args.Any(x => x.Equals("--sync-market-data", StringComparison.OrdinalIgnoreCase));
        from = default;
        to = default;
        symbols = [];
        error = null;
        if (!synchronize)
            return true;

        string? Value(string key) => Array.Find(args,
            x => x.StartsWith(key, StringComparison.OrdinalIgnoreCase))?[key.Length..];
        if (!DateTime.TryParseExact(Value("--from="), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out from) ||
            !DateTime.TryParseExact(Value("--to="), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out to) || from > to || to >= DateTime.UtcNow.Date)
        {
            error = "Error: --sync-market-data requires --from=yyyy-MM-dd and --to=yyyy-MM-dd, ordered and before today (UTC).";
            return false;
        }
        var symbolList = Value("--symbols=");
        symbols = symbolList is null
            ? StockUniverse.Symbols.Concat(BenchmarkUniverse.DefaultSymbols).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : symbolList.Split(',', StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (symbols.Count == 0 || symbols.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 20))
        {
            error = "Error: --symbols must contain comma-separated nonempty symbols of at most 20 characters.";
            return false;
        }
        return true;
    }
}
