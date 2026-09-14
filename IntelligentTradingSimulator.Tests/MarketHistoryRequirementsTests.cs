using IntelligentTradingSimulator.App.Simulation;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.Tests;

public sealed class MarketHistoryRequirementsTests
{
    [Fact]
    public void GetEffectiveStartDate_PrefersEarlierRequestedStartDate_WhenAlreadyFurtherBack()
    {
        var analysisDate = new DateTime(2025, 1, 31);
        var requestedStartDate = analysisDate.AddDays(-1000);

        var effectiveStartDate =
            MarketHistoryRequirements.GetEffectiveStartDate(
                requestedStartDate,
                analysisDate);

        Assert.Equal(requestedStartDate.Date, effectiveStartDate);
    }

    [Fact]
    public void GetEffectiveStartDate_WidensToWeek52Window_WhenRequestedStartDateIsMoreRecent()
    {
        var analysisDate = new DateTime(2025, 1, 31);
        var requestedStartDate = analysisDate.AddDays(-14);

        var effectiveStartDate =
            MarketHistoryRequirements.GetEffectiveStartDate(
                requestedStartDate,
                analysisDate);

        Assert.True(effectiveStartDate < requestedStartDate.Date);
        Assert.Equal(
            analysisDate.Date.AddDays(
                -Week52HighCalculator.DefaultTradingSessions * 2),
            effectiveStartDate);
    }

    // Covers weekends and US market holiday clusters over multi-year spans to
    // confirm the calendar-day approximation (2 calendar days per trading
    // session) is a safe overestimate: the widened window always contains at
    // least 252 actual trading sessions, even when the window crosses several
    // years with realistic holiday density.
    [Theory]
    [InlineData("2020-01-31")] // spans a leap year (2020) plus holidays
    [InlineData("2023-03-15")] // spans a New Year's cluster and DST changes
    [InlineData("2025-12-31")] // year-end analysis date, spans two calendar years
    public void GetEffectiveStartDate_ProducesWindowWithAtLeast252TradingSessions_AcrossWeekendsAndHolidays(
        string analysisDateText)
    {
        var analysisDate = DateTime.Parse(analysisDateText);
        var requestedStartDate = analysisDate.AddDays(-14);

        var effectiveStartDate =
            MarketHistoryRequirements.GetEffectiveStartDate(
                requestedStartDate,
                analysisDate);

        var tradingSessionCount =
            CountUsMarketTradingSessions(effectiveStartDate, analysisDate);

        Assert.True(
            tradingSessionCount >= Week52HighCalculator.DefaultTradingSessions,
            $"Expected at least {Week52HighCalculator.DefaultTradingSessions} " +
            $"trading sessions between {effectiveStartDate:yyyy-MM-dd} and " +
            $"{analysisDate:yyyy-MM-dd}, but only found {tradingSessionCount}.");
    }

    /// <summary>
    /// Counts weekday (Mon-Fri) calendar days between the two dates,
    /// excluding a representative set of US market holidays (New Year's Day,
    /// MLK Day, Presidents Day, Good Friday approximated, Memorial Day,
    /// Juneteenth, Independence Day, Labor Day, Thanksgiving, Christmas),
    /// approximately 9-10 holidays per year, to approximate real trading
    /// session density for the assertion above.
    /// </summary>
    private static int CountUsMarketTradingSessions(
        DateTime startDate,
        DateTime endDate)
    {
        var sessionCount = 0;

        for (var date = startDate.Date;
             date <= endDate.Date;
             date = date.AddDays(1))
        {
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (IsApproximateUsMarketHoliday(date))
            {
                continue;
            }

            sessionCount++;
        }

        return sessionCount;
    }

    private static bool IsApproximateUsMarketHoliday(DateTime date)
    {
        // Fixed-date holidays (approximate; observed-date shifting is
        // intentionally ignored since this only needs to be a realistic
        // holiday density, not an exact market calendar).
        if (date.Month == 1 && date.Day == 1) return true; // New Year's Day
        if (date.Month == 6 && date.Day == 19) return true; // Juneteenth
        if (date.Month == 7 && date.Day == 4) return true; // Independence Day
        if (date.Month == 12 && date.Day == 25) return true; // Christmas

        // Floating holidays approximated by month + weekday occurrence.
        if (date.Month == 1 && date.DayOfWeek == DayOfWeek.Monday &&
            date.Day is >= 15 and <= 21) return true; // MLK Day
        if (date.Month == 2 && date.DayOfWeek == DayOfWeek.Monday &&
            date.Day is >= 15 and <= 21) return true; // Presidents Day
        if (date.Month == 5 && date.DayOfWeek == DayOfWeek.Monday &&
            date.Day >= 25) return true; // Memorial Day
        if (date.Month == 9 && date.DayOfWeek == DayOfWeek.Monday &&
            date.Day <= 7) return true; // Labor Day
        if (date.Month == 11 && date.DayOfWeek == DayOfWeek.Thursday &&
            date.Day is >= 22 and <= 28) return true; // Thanksgiving
        if (date.Month == 4 && date.DayOfWeek == DayOfWeek.Friday &&
            date.Day is >= 1 and <= 21) return true; // Good Friday (approx.)

        return false;
    }
}
