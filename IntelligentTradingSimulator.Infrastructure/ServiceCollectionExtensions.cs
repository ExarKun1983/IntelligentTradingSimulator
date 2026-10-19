using IntelligentTradingSimulator.Core.Interfaces;
using IntelligentTradingSimulator.Core.Services;
using IntelligentTradingSimulator.Core.Training;
using IntelligentTradingSimulator.Infrastructure.MarketData;
using IntelligentTradingSimulator.Infrastructure.Persistence;
using IntelligentTradingSimulator.Infrastructure.Training;
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
        services.AddScoped<IFeatureSnapshotRepository, FeatureSnapshotRepository>();
        services.AddScoped<IFutureOutcomeLabelRepository, FutureOutcomeLabelRepository>();
        services.AddScoped<IMarketCandleRepository, MarketCandleRepository>();
        services.AddScoped<FutureOutcomeLabelWorkflow>();
        services.AddScoped<TrainingDatasetBuilder>();
        services.AddScoped<IBacktestSummaryReportService, BacktestSummaryReportService>();

        services.AddSingleton<IMarketDataProvider, YahooMarketDataProvider>();

        services.AddHttpClient();

        var primaryFmpSection = configuration?.GetSection(FinancialModelingPrepOptions.SectionName);
        var fmpSection = primaryFmpSection?.Exists() == true
            ? primaryFmpSection
            : configuration?.GetSection("FinancialModelingPrep");
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

                if (options.RequestTimeoutSeconds is < 1 or > FinancialModelingPrepOptions.MaximumRequestTimeoutSeconds)
                {
                    throw new InvalidOperationException(
                        $"FinancialModelingPrep request timeout must be between 1 and {FinancialModelingPrepOptions.MaximumRequestTimeoutSeconds} seconds.");
                }

                client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
                client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
            });

            services.AddTransient<IFundamentalDataProvider>(sp =>
                sp.GetRequiredService<FinancialModelingPrepFundamentalDataProvider>());
        }

        return services;
    }
}
