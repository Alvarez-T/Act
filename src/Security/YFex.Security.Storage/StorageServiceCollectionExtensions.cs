using Microsoft.Extensions.DependencyInjection;
using YFex.Security.Config;
using YFex.Security.Storage.Repositories;

namespace YFex.Security.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddYFexSecurityStorage(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<SecurityConfig>();
            return new SecurityDbConnectionFactory(config.DatabasePath);
        });

        services.AddSingleton<IEventRepository, EventRepository>();
        services.AddSingleton<IBaselineRepository, BaselineRepository>();
        services.AddSingleton<IApiCatalogRepository, ApiCatalogRepository>();
        services.AddSingleton<EventPersister>();

        return services;
    }
}
