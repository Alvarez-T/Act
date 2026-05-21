using Microsoft.Extensions.DependencyInjection;

namespace YFex.Persistence;

/// <summary>DI registration helpers for <c>YFex.Persistence</c>.</summary>
public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers <see cref="PersistenceService"/> as a singleton <see cref="IPersistenceService"/>
    /// and wires it into <see cref="PersistenceService.Configure"/> for static-facade access.
    /// Also registers <paramref name="storeFactory"/> as the <see cref="ISnapshotStore"/>.
    /// </summary>
    public static IServiceCollection AddYFexPersistence(
        this IServiceCollection services,
        Func<IServiceProvider, ISnapshotStore> storeFactory)
    {
        services.AddSingleton<ISnapshotStore>(storeFactory);
        services.AddSingleton<PersistenceService>(sp =>
        {
            var store   = sp.GetRequiredService<ISnapshotStore>();
            var service = new PersistenceService(store);
            PersistenceService.Configure(service);
            return service;
        });
        services.AddSingleton<IPersistenceService>(sp => sp.GetRequiredService<PersistenceService>());
        return services;
    }

    /// <summary>
    /// Variant that registers a pre-built <see cref="ISnapshotStore"/> instance.
    /// </summary>
    public static IServiceCollection AddYFexPersistence(
        this IServiceCollection services,
        ISnapshotStore store)
        => services.AddYFexPersistence(_ => store);
}
