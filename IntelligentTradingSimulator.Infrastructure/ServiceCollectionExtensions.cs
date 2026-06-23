using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<TradingSimulatorDbContext>(opts =>
            opts.UseSqlServer(connectionString));

        services.AddScoped<PortfolioRepository>();
        services.AddScoped<ObservationResultRepository>();
        services.AddScoped<PortfolioSnapshotRepository>();
        services.AddScoped<PositionSnapshotRepository>();

        services.AddSingleton<IMarketDataProvider, YahooMarketDataProvider>();

        return services;
    }
}
