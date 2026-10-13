namespace IntelligentTradingSimulator.Core.Models;

/// <summary>
/// Provider-neutral fundamental snapshot. Null metrics mean unavailable, not zero.
/// </summary>
public sealed class FundamentalData
{
    public required string Symbol { get; init; }

    /// <summary>The financial reporting period end, if known.</summary>
    public DateOnly? AsOfDate { get; init; }

    /// <summary>Year-over-year revenue growth in percentage points (10 means 10%).</summary>
    public decimal? RevenueGrowthPercent { get; init; }

    /// <summary>Year-over-year earnings-per-share growth in percentage points.</summary>
    public decimal? EpsGrowthPercent { get; init; }

    /// <summary>Net profit margin in percentage points.</summary>
    public decimal? Margins { get; init; }

    /// <summary>Total debt divided by total equity (1 means a ratio of 1:1).</summary>
    public decimal? Debt { get; init; }
}
