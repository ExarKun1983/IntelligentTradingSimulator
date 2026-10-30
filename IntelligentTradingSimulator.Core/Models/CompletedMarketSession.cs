using System.Collections.ObjectModel;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Core.Models;

/// <summary>A coherent set of daily closes; current New York dates are never executable.</summary>
public sealed class CompletedMarketSession
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    public const int MaximumAgeDays = 7;

    private CompletedMarketSession(DateOnly sessionDate, Dictionary<string, decimal> prices)
    {
        SessionDate = sessionDate;
        Prices = new ReadOnlyDictionary<string, decimal>(prices);
    }

    public DateOnly SessionDate { get; }
    public IReadOnlyDictionary<string, decimal> Prices { get; }

    public static DateOnly NewYorkDate(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, NewYork).DateTime);

    public void Validate(DateTimeOffset now)
    {
        var today = NewYorkDate(now);
        if (SessionDate >= today || today.DayNumber - SessionDate.DayNumber > MaximumAgeDays)
            throw new InvalidOperationException("Paper execution requires a completed, fresh New York daily session.");
    }

    public static CompletedMarketSession Create(
        IEnumerable<string> requiredSymbols,
        IReadOnlyDictionary<string, IReadOnlyList<MarketCandle>> candlesBySymbol,
        DateTimeOffset now)
    {
        var today = NewYorkDate(now);
        DateOnly? session = null;
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in requiredSymbols)
        {
            if (string.IsNullOrWhiteSpace(symbol) || prices.ContainsKey(symbol) ||
                !candlesBySymbol.TryGetValue(symbol, out var candles))
                throw new InvalidOperationException("Paper execution requires unambiguous data for every requested symbol.");
            var completed = candles.Where(x => DateOnly.FromDateTime(x.Date) < today)
                .OrderBy(x => x.Date).ToList();
            if (completed.Count == 0 ||
                completed.GroupBy(x => x.Date.Date).Any(x => x.Count() != 1))
                throw new InvalidOperationException("Paper execution requires unique completed daily candles.");
            if (completed.Any(x => !HasValidOhlcv(x)))
                throw new InvalidOperationException("Paper execution requires valid completed daily OHLCV history.");
            var latest = completed[^1];
            var date = DateOnly.FromDateTime(latest.Date);
            if (session.HasValue && session != date)
                throw new InvalidOperationException("Paper execution requires the same latest completed session for every symbol.");
            if (!TradingPrecisionPolicy.TryNormalizePrice(latest.Close, out var price))
                throw new InvalidOperationException("Paper execution requires valid daily OHLCV prices.");
            session = date;
            prices.Add(symbol, price);
        }
        if (!session.HasValue)
            throw new InvalidOperationException("Paper execution requires at least one symbol.");
        var result = new CompletedMarketSession(session.Value, prices);
        result.Validate(now);
        return result;
    }

    private static bool HasValidOhlcv(MarketCandle candle) =>
        candle.Open > 0 && candle.Low > 0 &&
        candle.High >= Math.Max(candle.Open, candle.Close) &&
        candle.Low <= Math.Min(candle.Open, candle.Close) &&
        candle.Volume >= 0;
}
