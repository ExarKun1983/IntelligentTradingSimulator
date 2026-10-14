using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Models;

namespace IntelligentTradingSimulator.Infrastructure.Reporting;

public sealed class BacktestSnapshotCsvExporter
{
    private const string Header =
        "Date,PortfolioValue,Cash,ProfitLoss,ReturnPercent,TradesExecuted,RebalanceExecuted";

    public async Task ExportAsync(
        string filePath,
        IEnumerable<BacktestSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var rows = OrderSnapshots(snapshots);
        cancellationToken.ThrowIfCancellationRequested();

        await using var writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
        await WriteAsync(writer, rows, cancellationToken);
    }

    public async Task WriteAsync(
        TextWriter writer,
        IEnumerable<BacktestSnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var rows = OrderSnapshots(snapshots);
        await writer.WriteAsync((Header + "\r\n").AsMemory(), cancellationToken);

        foreach (var snapshot in rows)
        {
            string[] fields =
            [
                snapshot.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Format(snapshot.PortfolioValue),
                Format(snapshot.Cash),
                Format(snapshot.ProfitLoss),
                Format(snapshot.ReturnPercent),
                snapshot.TradesExecuted.ToString(CultureInfo.InvariantCulture),
                snapshot.RebalanceExecuted ? "true" : "false"
            ];

            await writer.WriteAsync((string.Join(",", fields) + "\r\n").AsMemory(), cancellationToken);
        }
    }

    private static BacktestSnapshot[] OrderSnapshots(IEnumerable<BacktestSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var rows = snapshots.ToArray();
        if (rows.Any(s => s is null))
            throw new ArgumentException("Snapshots must not contain null records.", nameof(snapshots));
        return rows.OrderBy(s => s.Date).ToArray();
    }

    private static string Format(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}
