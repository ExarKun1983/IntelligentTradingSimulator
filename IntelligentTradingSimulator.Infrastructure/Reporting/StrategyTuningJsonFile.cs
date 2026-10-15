using System.Text.Json;
using System.Text.Json.Serialization;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.Reporting;

public static class StrategyTuningJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<StrategyTuningRequest> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        await using var source = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StrategyTuningRequest>(source, Options, cancellationToken)
            ?? throw new JsonException("A tuning request is required.");
    }

    public static async Task ExportAsync(
        string path, StrategyTuningReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var destination = File.Create(path);
        await JsonSerializer.SerializeAsync(destination, report, Options, cancellationToken);
    }
}
