using System.Threading.Tasks;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Core.Interfaces;

public interface IBacktestSummaryReportService
{
    Task<BacktestSummaryReportResult> GenerateAsync(int backtestRunId);
}
