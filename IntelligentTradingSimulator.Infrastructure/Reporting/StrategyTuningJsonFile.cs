using System.Text.Json;
using System.Text.Json.Serialization;
using IntelligentTradingSimulator.Core.Enums;
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
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(source, cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new JsonException(exception.Message, exception);
        }

        using (document)
        {
            var request = document.RootElement.Deserialize<StrategyTuningRequest>(Options)
                ?? throw new JsonException("A tuning request is required.");

            if (TryGetProperty(document.RootElement, "runs", out var runsElement) &&
                runsElement.ValueKind == JsonValueKind.Array && request.Runs is not null)
            {
                var index = 0;
                foreach (var runElement in runsElement.EnumerateArray())
                {
                    var run = index < request.Runs.Count ? request.Runs[index] : null;
                    if (run?.Summary is { IsSuccess: true, Status: BacktestRunStatus.Completed } &&
                        (!TryGetProperty(runElement, "summary", out var summaryElement) ||
                         !TryGetProperty(summaryElement, "maxDrawdownPercent", out _)))
                        throw new JsonException("Completed runs require an explicit maxDrawdownPercent.");
                    index++;
                }
            }

            return request;
        }
    }

    public static async Task ExportAsync(
        string path, StrategyTuningReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(destination, report, Options, cancellationToken);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
