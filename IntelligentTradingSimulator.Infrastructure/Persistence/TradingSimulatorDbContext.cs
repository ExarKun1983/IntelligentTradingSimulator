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

    public DbSet<BacktestRun>
    BacktestRuns =>
        Set<BacktestRun>();

    public DbSet<BacktestSnapshot>
    BacktestSnapshots =>
        Set<BacktestSnapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureObservationRun(modelBuilder);
        ConfigureObservationResult(modelBuilder);
        ConfigureBacktestRun(modelBuilder);
        ConfigureBacktestSnapshot(modelBuilder);
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

        entity.HasIndex(x => x.Symbol);

        entity.HasIndex(x => new { x.RankPosition });

        entity
        .HasOne(x => x.ObservationRun)
        .WithMany(x => x.Results)
        .HasForeignKey(x => x.ObservationRunId);
    }

    private static void ConfigureBacktestRun(
        ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BacktestRun>();

        entity.ToTable("BacktestRuns");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Name)
            .HasMaxLength(200)
            .IsRequired();

        entity.Property(x => x.StartDate)
            .IsRequired();

        entity.Property(x => x.EndDate)
            .IsRequired();

        entity.Property(x => x.InitialCapital)
            .HasPrecision(18, 4);

        entity.Property(x => x.FinalCapital)
            .HasPrecision(18, 4);

        entity.Property(x => x.TotalProfit)
            .HasPrecision(18, 4);

        entity.Property(x => x.TotalReturnPercent)
            .HasPrecision(18, 4);

        entity.Property(x => x.CreatedAt)
            .IsRequired();
    }

    private static void ConfigureBacktestSnapshot(
        ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BacktestSnapshot>();

        entity.ToTable("BacktestSnapshots");

        entity.HasKey(x => x.Id);

        entity.Property(x => x.Date)
            .IsRequired();

        entity.Property(x => x.PortfolioValue)
            .HasPrecision(18, 4);

        entity.Property(x => x.Cash)
            .HasPrecision(18, 4);

        entity.Property(x => x.ProfitLoss)
            .HasPrecision(18, 4);

        entity.Property(x => x.ReturnPercent)
            .HasPrecision(18, 4);

        entity.HasIndex(x => x.BacktestRunId);

        entity.HasIndex(x => new { x.BacktestRunId, x.Date });

        entity
            .HasOne(x => x.BacktestRun)
            .WithMany(x => x.Snapshots)
            .HasForeignKey(x => x.BacktestRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}