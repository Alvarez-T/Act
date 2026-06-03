using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.IndexedDb;

public static class IndexedDbMessagingExtensions
{
    /// <summary>
    /// Registers the IndexedDB persistent storage backend for Blazor WASM.
    /// Replaces the default in-memory <see cref="IClientStorage"/>, <see cref="IClientCache"/>,
    /// <see cref="IOutbox"/>, and <see cref="ISyncFailureLog"/> with storage-backed implementations
    /// that persist data across page reloads via IndexedDB.
    ///
    /// Call this after <c>AddYFexMessagingRpcClient</c>.
    /// </summary>
    public static IServiceCollection AddYFexIndexedDBStorage(
        this IServiceCollection services,
        Action<IndexedDbStorageOptions>? configure = null)
    {
        var opts = new IndexedDbStorageOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);
        services.AddSingleton<IndexedDBClientStorage>();
        services.AddSingleton<StorageBackedOutbox>();
        services.AddSingleton<StorageBackedSyncFailureLog>();

        // Register the raw storage under the well-known inner key for the encryption decorator.
        services.AddKeyedSingleton<IClientStorage>(StorageServiceKeys.Inner,
            (sp, _) => sp.GetRequiredService<IndexedDBClientStorage>());

        services.Replace(ServiceDescriptor.Singleton<IClientStorage>(
            sp => sp.GetRequiredService<IndexedDBClientStorage>()));

        services.Replace(ServiceDescriptor.Singleton<IClientCache>(
            sp => new StorageBackedClientCache(sp.GetRequiredService<IClientStorage>())));

        services.Replace(ServiceDescriptor.Singleton<IOutbox>(sp =>
        {
            var outbox = sp.GetRequiredService<StorageBackedOutbox>();
            outbox.SetFailureLog(sp.GetRequiredService<StorageBackedSyncFailureLog>());
            return outbox;
        }));

        services.Replace(ServiceDescriptor.Singleton<ISyncFailureLog>(sp =>
        {
            var log = sp.GetRequiredService<StorageBackedSyncFailureLog>();
            log.SetOutbox(sp.GetRequiredService<StorageBackedOutbox>());
            return log;
        }));

        services.Replace(ServiceDescriptor.Singleton<IWritableSyncFailureLog>(
            sp => sp.GetRequiredService<StorageBackedSyncFailureLog>()));

        return services;
    }
}
