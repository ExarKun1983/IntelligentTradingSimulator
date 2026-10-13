using System.Text.Json;
using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.MarketData;

/// <summary>
/// Loads locally persisted snapshots without contacting an external provider.
/// The file contains a JSON array with at most one snapshot per symbol.
/// </summary>
public sealed class JsonFundamentalDataProvider : IFundamentalDataProvider
{
    private readonly string _filePath;

    public JsonFundamentalDataProvider(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public async Task<FundamentalData?> GetFundamentalDataAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = File.OpenRead(_filePath);
        var snapshots = await JsonSerializer.DeserializeAsync<FundamentalData[]>(
            stream, cancellationToken: cancellationToken)
            ?? throw new JsonException("Fundamental snapshots must be a JSON array.");

        var bySymbol = new Dictionary<string, FundamentalData>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Symbol))
                throw new JsonException("Each fundamental snapshot must have a symbol.");

            if (!bySymbol.TryAdd(snapshot.Symbol.Trim(), snapshot))
                throw new JsonException("Fundamental snapshots must have unique symbols.");
        }

        return bySymbol.GetValueOrDefault(symbol.Trim());
    }
}
