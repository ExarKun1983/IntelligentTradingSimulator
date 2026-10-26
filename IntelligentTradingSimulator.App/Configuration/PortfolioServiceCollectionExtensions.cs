using IntelligentTradingSimulator.Core.Models;
using IntelligentTradingSimulator.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace IntelligentTradingSimulator.App.Configuration;

public static class PortfolioServiceCollectionExtensions
{
    public static IServiceCollection AddPortfolioServices(
        this IServiceCollection services,
        StrategyConfiguration activeStrategy)
    {
        services.AddSingleton(
            _ => new PortfolioRecommendationService(activeStrategy.MaxPositions));
        services.AddSingleton(
            _ => new PortfolioExecutionService(
                activeStrategy.MaxPositionWeight,
                activeStrategy.MaxPositions));
        services.AddSingleton<TradingEngine>();
        services.AddSingleton<PortfolioPerformanceService>();
        services.AddSingleton<PortfolioAllocationService>();
        services.AddSingleton(
            sp => new PortfolioAllocationEngine(
                sp.GetRequiredService<PortfolioAllocationService>(),
                activeStrategy.MaxPositionWeight));
        services.AddSingleton<PortfolioContributionService>();
        services.AddSingleton<PortfolioRebalancingService>();
        return services;
    }
}
