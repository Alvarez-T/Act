using Microsoft.Extensions.DependencyInjection;
using YFex.Tenancy;

namespace YFex.Persistence.Tenancy;

/// <summary>Opt-in switch that turns on tenant isolation for YFex.Persistence.</summary>
public static class PersistenceTenancyExtensions
{
    /// <summary>
    /// Partitions the registered <see cref="IKeyValueStore"/> (required), <see cref="ISyncLocalStore"/>
    /// (if registered) and <see cref="ICache"/> (if registered and <see cref="PersistenceIsolationOptions.IsolateCache"/>)
    /// by the scope in effect. Enables isolation with the default policy if <c>Isolation()</c> was not called.
    /// Call after the storage backend is registered (<c>AddYFexPersistence</c>), like <c>AddYFexStorageEncryption</c>.
    /// </summary>
    /// <remarks>
    /// If <see cref="ICache"/> is a <see cref="KeyValueCache"/> over the same (now scoped) store, set
    /// <see cref="PersistenceIsolationOptions.IsolateCache"/> to false — the store already partitions it.
    /// Tags on <see cref="PersistenceIsolationOptions.SharedKeyPrefixes"/> keys should themselves start with a
    /// shared prefix so tag eviction resolves to the same (unpartitioned) area.
    /// </remarks>
    public static YFexTenancyBuilder IsolatePersistence(this YFexTenancyBuilder tenancy, Action<PersistenceIsolationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(tenancy);
        var options = new PersistenceIsolationOptions();
        configure?.Invoke(options);
        tenancy.EnsureIsolation();

        var services = tenancy.Services;
        services.AddSingleton(sp =>
        {
            var store = Scoper(sp, options.Partition, options.SharedKeyPrefixes);
            var cache = options.CachePartition is null ? store : Scoper(sp, options.CachePartition, options.SharedKeyPrefixes);
            return new Scopers(store, cache);
        });

        if (!Decorate<IKeyValueStore>(services, (sp, inner) => new ScopedKeyValueStore(inner, sp.GetRequiredService<Scopers>().Store)))
            throw new InvalidOperationException(
                "No IKeyValueStore registered. Register the persistence backend (AddYFexPersistence) before IsolatePersistence().");
        Decorate<ISyncLocalStore>(services, (sp, inner) => new ScopedSyncLocalStore(inner, sp.GetRequiredService<Scopers>().Store));
        if (options.IsolateCache)
            Decorate<ICache>(services, (sp, inner) => new ScopedCache(inner, sp.GetRequiredService<Scopers>().Cache));

        return tenancy;
    }

    // null window → the global partitioner; otherwise a private partitioner over a copy of the global policy.
    private static PersistenceKeyScoper Scoper(IServiceProvider sp, Action<ScopePartitionOptions>? window, IEnumerable<string> shared)
    {
        IScopePartitioner partitioner;
        if (window is null)
        {
            partitioner = sp.GetRequiredService<IScopePartitioner>();
        }
        else
        {
            var copy = sp.GetRequiredService<ScopePartitionOptions>().Clone();
            window(copy);
            partitioner = new ScopeKeyPartitioner(copy, sp.GetService<IScopeContext>());
        }
        return new PersistenceKeyScoper(partitioner, shared);
    }

    // Per-registration holder so two IsolatePersistence windows (store vs cache) don't share one DI slot.
    private sealed record Scopers(PersistenceKeyScoper Store, PersistenceKeyScoper Cache);

    // Replaces the last registration of TService with a decorator over it, keeping its lifetime.
    private static bool Decorate<TService>(IServiceCollection services, Func<IServiceProvider, TService, TService> decorate)
        where TService : class
    {
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(TService) && !d.IsKeyedService);
        if (inner is null) return false;
        services.Remove(inner);
        services.Add(ServiceDescriptor.Describe(typeof(TService), sp => decorate(sp, ResolveInner<TService>(sp, inner)), inner.Lifetime));
        return true;
    }

    private static TService ResolveInner<TService>(IServiceProvider sp, ServiceDescriptor descriptor) where TService : class
    {
        if (descriptor.ImplementationInstance is TService instance) return instance;
        if (descriptor.ImplementationFactory is { } factory) return (TService)factory(sp);
        if (descriptor.ImplementationType is { } type) return (TService)ActivatorUtilities.CreateInstance(sp, type);
        throw new InvalidOperationException($"Unable to resolve the inner {typeof(TService).Name} to decorate.");
    }
}
