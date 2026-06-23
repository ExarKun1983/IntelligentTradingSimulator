using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class TradingSimulatorDbContextFactory
    : IDesignTimeDbContextFactory<TradingSimulatorDbContext>
{
    public TradingSimulatorDbContext CreateDbContext(string[] args)
    {
        var builder =
            new DbContextOptionsBuilder<TradingSimulatorDbContext>();

        builder.UseSqlServer(
            "Server=192.168.1.45,1433;Database=IntelligentTradingSimulator;User Id=sa;Password=Dom43pak!@#;TrustServerCertificate=True;");

        return new TradingSimulatorDbContext(
            builder.Options);
    }
}
