using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZiggyCreatures.Caching.Fusion;

namespace YFex.Persistence;

/// <summary>DI registration helpers for <c>YFex.Persistence</c>.</summary>
public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Registers the persistence stack around an <see cref="IKeyValueStore"/>:
    /// the store, a default <see cref="IPersistenceSerializer"/> (MemoryPack), a default
    /// <see cref="ICache"/> (<see cref="KeyValueCache"/> over the store), and the
    /// <see cref="PersistenceService"/> snapshot orchestrator (also wired into the static
    /// <see cref="PersistenceService.Current"/> facade).
    /// </summary>
    public static IServiceCollection AddYFexPersistence(
        this IServiceCollection services,
        Func<IServiceProvider, IKeyValueStore> storeFactory)
    {
        services.AddSingleton<IKeyValueStore>(storeFactory);
        services.TryAddSingleton<IPersistenceSerializer>(MemoryPackPersistenceSerializer.Instance);
        services.TryAddSingleton<ICache, InMemoryCache>();

        services.AddSingleton<PersistenceService>(sp =>
        {
            var store   = sp.GetRequiredService<IKeyValueStore>();
            var service = new PersistenceService(store);
            PersistenceService.Configure(service);
            return service;
        });
        services.AddSingleton<IPersistenceService>(sp => sp.GetRequiredService<PersistenceService>());
        return services;
    }

    /// <summary>Variant that registers a pre-built <see cref="IKeyValueStore"/> instance.</summary>
    public static IServiceCollection AddYFexPersistence(
        this IServiceCollection services,
        IKeyValueStore store)
        => services.AddYFexPersistence(_ => store);

    /// <summary>
    /// Opt-in: registers a FusionCache-backed <see cref="ICache"/> (<see cref="FusionCacheAdapter"/>)
    /// with a <b>bounded L1</b> — entries are counted by <see cref="CacheEntryOptions.Size"/> against
    /// <paramref name="l1SizeLimit"/> and evicted by <see cref="CacheEntryOptions.Priority"/> under
    /// memory pressure. Fail-safe is enabled by default. L1-only: to add a durable L2, wire an
    /// <c>IDistributedCache</c> (e.g. <see cref="KeyValueStoreDistributedCache"/>) plus a FusionCache
    /// serializer and call <c>WithRegisteredDistributedCache()</c>.
    /// </summary>
    /// <remarks>
    /// Note: <see cref="FusionCacheAdapter"/> does not support <see cref="ICache.GetKeysWithPrefixAsync"/>.
    /// Keep <see cref="KeyValueCache"/> where prefix-based batch invalidation is required.
    /// </remarks>
    public static IServiceCollection AddYFexFusionCache(
        this IServiceCollection services,
        long l1SizeLimit = 10_000,
        TimeSpan? defaultDuration = null)
    {
        var duration = defaultDuration ?? TimeSpan.FromMinutes(5);

        services.AddFusionCache()
            .WithDefaultEntryOptions(o =>
            {
                o.Duration = duration;
                o.IsFailSafeEnabled = true;
            })
            .WithMemoryCache(new MemoryCache(new MemoryCacheOptions
            {
                SizeLimit = l1SizeLimit,
                CompactionPercentage = 0.25,
            }));

        services.TryAddSingleton<ICache>(sp =>
            new FusionCacheAdapter(sp.GetRequiredService<IFusionCache>(), duration));

        return services;
    }
}
