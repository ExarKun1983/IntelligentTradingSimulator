using IntelligentTradingSimulator.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace IntelligentTradingSimulator.Infrastructure.Persistence;

public sealed class TradingSimulatorDbContext : DbContext
{
    public TradingSimulatorDbContext(
        DbContextOptions<TradingSimulatorDbContext> options)
        : base(options)
    {
    }

    public DbSet<Portfolio> Portfolios => Set<Portfolio>();

    public DbSet<Position> Positions => Set<Position>();

    public DbSet<Trade> Trades => Set<Trade>();

    public DbSet<ObservationRun> ObservationRuns =>
        Set<ObservationRun>();

    public DbSet<ObservationResultEntity> ObservationResults =>
        Set<ObservationResultEntity>();

    public DbSet<PortfolioSnapshot>
    PortfolioSnapshots =>
        Set<PortfolioSnapshot>();

    public DbSet<PositionSnapshot>
    PositionSnapshots =>
        Set<PositionSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureObservationRun(modelBuilder);
        ConfigureObservationResult(modelBuilder);
    }

    private static void ConfigureObservationRun(
    ModelBuilder modelBuilder)
    {
        var entity =
            modelBuilder.Entity<ObservationRun>();

        entity.ToTable("ObservationRuns");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.CreatedAt)
            .IsRequired();
    }

    private static void ConfigureObservationResult(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ObservationResultEntity>();

        entity.ToTable("ObservationResults");

        entity.HasKey(x => x.Id);

        //entity.Property(x => x.ObservationDate)
        //    .IsRequired();

        entity.Property(x => x.Symbol)
            .HasMaxLength(20)
            .IsRequired();

        entity.Property(x => x.MomentumPercent)
            .HasPrecision(18, 4);

        entity.Property(x => x.VolumeTrendPercent)
            .HasPrecision(18, 4);

        entity.Property(x => x.VolatilityPercent)
            .HasPrecision(18, 4);

        entity.Property(x => x.Score)
            .HasPrecision(18, 4);

        entity.Property(x => x.RankPosition)
            .IsRequired();

        entity.Property(x => x.IsAboveSma5)
            .IsRequired();

        //entity.HasIndex(x => x.ObservationDate);

        entity.HasIndex(x => x.Symbol);

        entity.HasIndex(x => new
        {
            //x.ObservationDate,
            x.RankPosition
        });

        entity
        .HasOne(x => x.ObservationRun)
        .WithMany(x => x.Results)
        .HasForeignKey(x => x.ObservationRunId);
    }
}