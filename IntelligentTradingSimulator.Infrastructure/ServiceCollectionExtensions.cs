using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        return services.AddInfrastructure(connectionString, configuration: null);
    }

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        string connectionString,
        IConfiguration? configuration)
    {
        services.AddDbContext<TradingSimulatorDbContext>(opts =>
            opts.UseSqlServer(connectionString));

        services.AddScoped<PortfolioRepository>();
        services.AddScoped<ObservationResultRepository>();
        services.AddScoped<PortfolioSnapshotRepository>();
        services.AddScoped<PositionSnapshotRepository>();
        services.AddScoped<IBacktestRunRepository, BacktestRunRepository>();
        services.AddScoped<IBacktestSnapshotRepository, BacktestSnapshotRepository>();
        services.AddScoped<IBacktestSummaryReportService, BacktestSummaryReportService>();

        services.AddSingleton<IMarketDataProvider, YahooMarketDataProvider>();

        services.AddHttpClient();

        var fmpSection = configuration?.GetSection(FinancialModelingPrepOptions.SectionName)
            ?? configuration?.GetSection("FinancialModelingPrep");
        if (fmpSection is not null && fmpSection.Exists())
        {
            services.Configure<FinancialModelingPrepOptions>(fmpSection);
        }
        else
        {
            services.Configure<FinancialModelingPrepOptions>(_ => { });
        }

        var fmpApiKey = fmpSection?[nameof(FinancialModelingPrepOptions.ApiKey)];
        if (!string.IsNullOrWhiteSpace(fmpApiKey))
        {
            services.AddHttpClient<FinancialModelingPrepFundamentalDataProvider>((serviceProvider, client) =>
            {
                var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FinancialModelingPrepOptions>>().Value;
                var baseUrl = string.IsNullOrWhiteSpace(options.BaseUrl)
                    ? FinancialModelingPrepOptions.DefaultBaseUrl
                    : options.BaseUrl;

                client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
            });

            services.AddTransient<IFundamentalDataProvider>(sp =>
                sp.GetRequiredService<FinancialModelingPrepFundamentalDataProvider>());
        }

        return services;
    }
}
