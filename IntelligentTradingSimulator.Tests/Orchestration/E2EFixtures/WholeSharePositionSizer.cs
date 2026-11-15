namespace IntelligentTradingSimulator.Tests.Orchestration.E2EFixtures;

internal static class WholeSharePositionSizer
{
    public static decimal Calculate(decimal budget, decimal unitPrice)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unitPrice);

        return decimal.Floor(budget / unitPrice);
    }
}
