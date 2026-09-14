using System;
using IntelligentTradingSimulator.Core.Services;

namespace IntelligentTradingSimulator.App.Simulation;

public static class MarketHistoryRequirements
{
    private const int CalendarDaysPerTradingSession = 2;

    public static DateTime GetEffectiveStartDate(
        DateTime requestedStartDate,
        DateTime analysisDate)
    {
        var week52StartDate =
            analysisDate.Date.AddDays(
                -Week52HighCalculator.DefaultTradingSessions
                * CalendarDaysPerTradingSession);

        return requestedStartDate.Date < week52StartDate
            ? requestedStartDate.Date
            : week52StartDate;
    }
}
