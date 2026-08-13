using Microsoft.Extensions.DependencyInjection;

namespace YFex.Security.Analysis;

public static class AnalysisServiceCollectionExtensions
{
    public static IServiceCollection AddYFexSecurityAnalysis(this IServiceCollection services)
    {
        services.AddSingleton<BaselineBuilder>();
        services.AddHostedService<AnomalyDetector>();
        services.AddSingleton<ReportGenerator>();
        return services;
    }
}
