namespace IntelligentTradingSimulator.Core.Models;

public sealed class PaperPortfolioState
{
    public const string MainIdentity = "main";
    public string Identity { get; set; } = MainIdentity;
    public int PortfolioId { get; set; }
    public Portfolio Portfolio { get; set; } = null!;
    public DateOnly? LastCompletedSession { get; set; }
}

public sealed class PaperPortfolioExecution
{
    public string Identity { get; set; } = PaperPortfolioState.MainIdentity;
    public DateOnly SessionDate { get; set; }
    public DateTime CompletedAtUtc { get; set; }
}

public enum PaperExecutionOutcome
{
    Executed,
    AlreadyExecuted,
    OlderSession
}
