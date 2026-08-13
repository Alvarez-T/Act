using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Persistence;

namespace YFex.Persistence.IndexedDb;

public static class IndexedDbServiceExtensions
{
    /// <summary>
    /// Registers the IndexedDB <see cref="IKeyValueStore"/> for Blazor WASM (persists across
    /// page reloads). Consumers that layer caches/queues on top resolve <see cref="IKeyValueStore"/>.
    /// </summary>
    public static IServiceCollection AddYFexIndexedDbStore(
        this IServiceCollection services,
        Action<IndexedDbStorageOptions>? configure = null)
    {
        var opts = new IndexedDbStorageOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);
        services.AddSingleton<IndexedDbKeyValueStore>();
        services.Replace(ServiceDescriptor.Singleton<IKeyValueStore>(
            sp => sp.GetRequiredService<IndexedDbKeyValueStore>()));
        return services;
    }
}
