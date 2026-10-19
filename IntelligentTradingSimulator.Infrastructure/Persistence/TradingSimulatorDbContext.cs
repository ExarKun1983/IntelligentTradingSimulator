using IntelligentTradingSimulator.Core.Models;
using System.Text.Json;
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

    public DbSet<FeatureSnapshot> FeatureSnapshots => Set<FeatureSnapshot>();
    public DbSet<FutureOutcomeLabel> FutureOutcomeLabels => Set<FutureOutcomeLabel>();
    public DbSet<MarketCandleEntity> MarketCandles => Set<MarketCandleEntity>();
    public DbSet<MarketDataCoverage> MarketDataCoverage => Set<MarketDataCoverage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureObservationRun(modelBuilder);
        ConfigureObservationResult(modelBuilder);
        ConfigureBacktestRun(modelBuilder);
        ConfigureBacktestSnapshot(modelBuilder);
        ConfigureFeatureSnapshot(modelBuilder);
        ConfigureFutureOutcomeLabel(modelBuilder);
        ConfigureMarketData(modelBuilder);
    }

    private static void ConfigureMarketData(ModelBuilder modelBuilder)
    {
        var candle = modelBuilder.Entity<MarketCandleEntity>();
        candle.ToTable("MarketCandles");
        candle.HasKey(x => x.Id);
        candle.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        candle.Property(x => x.Source).HasMaxLength(50).IsRequired();
        candle.Property(x => x.PriceAdjustmentMode).HasMaxLength(50).IsRequired();
        candle.Property(x => x.Date).HasColumnType("date");
        candle.Property(x => x.Open).HasPrecision(28, 12);
        candle.Property(x => x.High).HasPrecision(28, 12);
        candle.Property(x => x.Low).HasPrecision(28, 12);
        candle.Property(x => x.Close).HasPrecision(28, 12);
        candle.Property(x => x.Volume).HasPrecision(28, 4);
        candle.HasIndex(x => new { x.Symbol, x.Source, x.Date }).IsUnique();

        var coverage = modelBuilder.Entity<MarketDataCoverage>();
        coverage.ToTable("MarketDataCoverage");
        coverage.HasKey(x => x.Id);
        coverage.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        coverage.Property(x => x.Source).HasMaxLength(50).IsRequired();
        coverage.Property(x => x.PriceAdjustmentMode).HasMaxLength(50).IsRequired();
        coverage.Property(x => x.From).HasColumnType("date");
        coverage.Property(x => x.To).HasColumnType("date");
        coverage.HasIndex(x => new { x.Symbol, x.Source, x.PriceAdjustmentMode, x.From, x.To }).IsUnique();
    }

    private static void ConfigureFutureOutcomeLabel(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<FutureOutcomeLabel>();
        entity.ToTable("FutureOutcomeLabels");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.BenchmarkSymbol).HasMaxLength(20).IsRequired();
        entity.Property(x => x.ReturnPercent).HasPrecision(28, 12);
        entity.Property(x => x.BenchmarkReturnPercent).HasPrecision(28, 12);
        entity.HasIndex(x => new { x.FeatureSnapshotId, x.HorizonTradingDays, x.BenchmarkSymbol }).IsUnique();
        entity.HasOne<FeatureSnapshot>().WithMany().HasForeignKey(x => x.FeatureSnapshotId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureFeatureSnapshot(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<FeatureSnapshot>();
        entity.ToTable("FeatureSnapshots");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Symbol).HasMaxLength(20).IsRequired();
        entity.Property(x => x.StrategyId).HasMaxLength(100).IsRequired();
        entity.Property(x => x.Score).HasPrecision(28, 12);
        entity.Property(x => x.Features)
            .HasConversion(
                value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => JsonSerializer.Deserialize<StockRankingFeatures>(value, (JsonSerializerOptions?)null)!)
            .IsRequired();
        entity.Property(x => x.StrategyConfigurationJson).IsRequired();
        entity.HasIndex(x => new { x.Symbol, x.AnalysisDate, x.StrategyId })
            .IsUnique().HasFilter("[BacktestRunId] IS NULL");
        entity.HasIndex(x => new { x.Symbol, x.AnalysisDate, x.StrategyId, x.BacktestRunId })
            .IsUnique().HasFilter("[BacktestRunId] IS NOT NULL");
        entity.HasIndex(x => x.AnalysisDate);
        entity.HasOne<BacktestRun>().WithMany().HasForeignKey(x => x.BacktestRunId)
            .OnDelete(DeleteBehavior.Cascade);
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

        entity.Property(x => x.StrategyId)
            .HasMaxLength(100)
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

        entity.Property(x => x.Status)
            .IsRequired();

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

        entity.Property(x => x.TradesExecuted)
            .IsRequired();

        entity.Property(x => x.RebalanceExecuted)
            .IsRequired();

        entity.HasIndex(x => x.BacktestRunId);

        entity.HasIndex(x => new { x.BacktestRunId, x.Date });

        entity
            .HasOne(x => x.BacktestRun)
            .WithMany(x => x.Snapshots)
            .HasForeignKey(x => x.BacktestRunId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}