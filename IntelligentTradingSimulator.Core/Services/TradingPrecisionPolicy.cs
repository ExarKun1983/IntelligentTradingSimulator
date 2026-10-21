namespace IntelligentTradingSimulator.Core.Services;

public static class TradingPrecisionPolicy
{
    public const int QuantityPrecision = 28;
    public const int QuantityScale = 8;
    public const int PricePrecision = 28;
    public const int PriceScale = 12;
    public const int CashPrecision = 28;
    public const int CashScale = 8;
    public const int AveragePricePrecision = 28;
    public const int AveragePriceScale = 12;
    public const int SnapshotPrecision = 28;
    public const int SnapshotScale = 8;
    public const int PercentagePrecision = 18;
    public const int PercentageScale = 8;
    public const int ReportScale = 4;

    private const decimal MaximumQuantityOrCash = 100_000_000_000_000_000_000m;
    private const decimal MaximumPrice = 10_000_000_000_000_000m;

    public static bool TryNormalizeQuantity(decimal value, out decimal quantity)
    {
        quantity = Math.Round(value, QuantityScale, MidpointRounding.AwayFromZero);
        return quantity > 0m && quantity < MaximumQuantityOrCash;
    }

    public static bool TryNormalizePrice(decimal value, out decimal price)
    {
        price = Math.Round(value, PriceScale, MidpointRounding.AwayFromZero);
        return price > 0m && price < MaximumPrice;
    }

    public static bool TryNormalizeCash(decimal value, out decimal cash)
    {
        cash = Math.Round(value, CashScale, MidpointRounding.AwayFromZero);
        return cash > 0m && cash < MaximumQuantityOrCash;
    }

    public static bool TryNormalizeNotional(decimal value, out decimal notional)
    {
        notional = Math.Round(value, CashScale, MidpointRounding.AwayFromZero);
        return notional > 0m && notional < MaximumQuantityOrCash;
    }

    public static decimal RoundAveragePrice(decimal value) =>
        Math.Round(value, AveragePriceScale, MidpointRounding.AwayFromZero);

    public static decimal RoundReport(decimal value) =>
        Math.Round(value, ReportScale, MidpointRounding.AwayFromZero);

    public static decimal FloorQuantity(decimal value) =>
        Math.Round(value, QuantityScale, MidpointRounding.ToZero);

    public static bool CashIsInRange(decimal value) =>
        value > -MaximumQuantityOrCash && value < MaximumQuantityOrCash;

    public static bool PriceIsInRange(decimal value) =>
        value >= 0m && value < MaximumPrice;
}
