using System.Globalization;
using System.Text;
using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Reporting;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Tests;

public sealed class BacktestSnapshotCsvExporterTests
{
    private const string Header =
        "Date,PortfolioValue,Cash,ProfitLoss,ReturnPercent,TradesExecuted,RebalanceExecuted\r\n";

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task WriteAsync_UsesStableSchemaAndInvariantValues(string cultureName)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            using var writer = new StringWriter();

            await new BacktestSnapshotCsvExporter().WriteAsync(writer,
            [
                new()
                {
                    Date = new DateTime(2024, 1, 2),
                    PortfolioValue = 1000.1234567890123456789012345m,
                    Cash = 200.25m,
                    ProfitLoss = -10.75m,
                    ReturnPercent = -1.075m,
                    TradesExecuted = 2,
                    RebalanceExecuted = true
                },
                new() { Date = new DateTime(2024, 1, 3), ProfitLoss = 10.5m, ReturnPercent = 1.05m }
            ]);

            Assert.Equal(Header +
                "2024-01-02,1000.1234567890123456789012345,200.25,-10.75,-1.075,2,true\r\n" +
                "2024-01-03,0,0,10.5,1.05,0,false\r\n", writer.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task WriteAsync_OrdersRowsWithoutFillingGapsOrDeduplicating()
    {
        using var writer = new StringWriter();
        await new BacktestSnapshotCsvExporter().WriteAsync(writer,
        [
            new() { Date = new DateTime(2024, 1, 5), PortfolioValue = 105m },
            new() { Date = new DateTime(2024, 1, 2), PortfolioValue = 101m },
            new() { Date = new DateTime(2024, 1, 2), PortfolioValue = 102m }
        ]);

        Assert.Equal(Header +
            "2024-01-02,101,0,0,0,0,false\r\n" +
            "2024-01-02,102,0,0,0,0,false\r\n" +
            "2024-01-05,105,0,0,0,0,false\r\n", writer.ToString());
    }

    [Fact]
    public async Task WriteAsync_EmptyCollectionWritesOnlyHeader()
    {
        using var writer = new StringWriter();
        await new BacktestSnapshotCsvExporter().WriteAsync(writer, []);
        Assert.Equal(Header, writer.ToString());
    }

    [Fact]
    public async Task ExportAsync_ExportsOnlyPersistedSnapshotsForSelectedRun()
    {
        var options = new DbContextOptionsBuilder<TradingSimulatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var dbContext = new TradingSimulatorDbContext(options);
        var repository = new BacktestSnapshotRepository(dbContext);
        await repository.AddRangeAsync(
        [
            new() { BacktestRunId = 42, Date = new DateTime(2024, 1, 5), PortfolioValue = 105m },
            new() { BacktestRunId = 43, Date = new DateTime(2024, 1, 3), PortfolioValue = 999m },
            new() { BacktestRunId = 42, Date = new DateTime(2024, 1, 2), PortfolioValue = 101m }
        ]);
        dbContext.ChangeTracker.Clear();
        var path = Path.Combine(Path.GetTempPath(), $"snapshots-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "old content");
            var snapshots = await repository.GetByRunIdAsync(42);

            await new BacktestSnapshotCsvExporter().ExportAsync(path, snapshots);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
            Assert.Equal(Header +
                "2024-01-02,101,0,0,0,0,false\r\n" +
                "2024-01-05,105,0,0,0,0,false\r\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExportAsync_InvalidCollectionDoesNotOverwriteFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapshots-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, "existing");
            var exporter = new BacktestSnapshotCsvExporter();

            await Assert.ThrowsAsync<ArgumentNullException>(() => exporter.ExportAsync(path, null!));
            await Assert.ThrowsAsync<ArgumentException>(() => exporter.ExportAsync(path, [new(), null!]));

            Assert.Equal("existing", await File.ReadAllTextAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ExportAsync_RejectsBlankPath(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new BacktestSnapshotCsvExporter().ExportAsync(path, []));
    }

    [Fact]
    public async Task ExportAsync_CancellationDoesNotCreateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snapshots-{Guid.NewGuid():N}.csv");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BacktestSnapshotCsvExporter().ExportAsync(path, [new()], new CancellationToken(true)));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task WriteAsync_RejectsNullWriter()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            new BacktestSnapshotCsvExporter().WriteAsync(null!, []));
    }
}
