using System;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class TradingSimulatorDbContextFactory
    : IDesignTimeDbContextFactory<TradingSimulatorDbContext>
{
    public TradingSimulatorDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("TRADING_DB_CONNECTION")
            ?? throw new InvalidOperationException(
                "Set the TRADING_DB_CONNECTION environment variable before running EF migrations.");

        var builder =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>();

        builder.UseSqlServer(connectionString);

        return new TradingSimulatorDbContext(
            builder.Options);
    }
}
