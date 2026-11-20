namespace IntelligentTradingSimulator.Core.Models;

public sealed class AiScoringException : Exception
{
    public AiScoringException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
