using System.Text.Json;
using IntelligentTradingSimulator.Core.Training;

namespace IntelligentTradingSimulator.Infrastructure.Training;

public static class OfflineEvaluationJsonExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static Task ExportAsync(
        OfflineEvaluationReport report, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, report, Options, cancellationToken);
    }
}
