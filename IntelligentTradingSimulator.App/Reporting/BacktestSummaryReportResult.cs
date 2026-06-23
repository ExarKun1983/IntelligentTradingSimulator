namespace IntelligentTradingSimulator.App.Reporting;

public sealed class BacktestSummaryReportResult
{
    public int RunId { get; set; }

    public BacktestSummaryReport? Report { get; set; }

    public string? ErrorMessage { get; set; }

    public bool IsSuccess => Report is not null
        && string.IsNullOrWhiteSpace(ErrorMessage);
}
