using System.Text.Json;
using IntelligentTradingSimulator.Core.Training;

namespace IntelligentTradingSimulator.Infrastructure.Training;

public static class TrainingDatasetJsonExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static Task ExportAsync(
        TrainingDataset dataset, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, dataset, Options, cancellationToken);
    }
}
