using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Sqlite;

public static class SqliteMessagingExtensions
{
    /// <summary>
    /// Registers the SQLite persistent storage backend. Call this after
    /// <c>AddYFexMessagingRpcClient</c> to replace the default in-memory registrations.
    /// </summary>
    public static IServiceCollection AddYFexSqliteStorage(
        this IServiceCollection services,
        Action<SqliteStorageOptions>? configure = null)
    {
        var opts = new SqliteStorageOptions();
        configure?.Invoke(opts);
        services.AddSingleton(opts);
        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteClientStorage>();
        services.AddSingleton<SqliteOutbox>();
        services.AddSingleton<SqliteSyncFailureLog>();

        // Register the raw storage under the well-known inner key so the encryption decorator
        // can wrap it without circular DI resolution.
        services.AddKeyedSingleton<IClientStorage>(StorageServiceKeys.Inner,
            (sp, _) => sp.GetRequiredService<SqliteClientStorage>());

        // Replace in-memory defaults with SQLite implementations.
        // Using Replace so call-order (before or after AddYFexMessagingRpcClient) is irrelevant.
        services.Replace(ServiceDescriptor.Singleton<IClientStorage>(
            sp => sp.GetRequiredService<SqliteClientStorage>()));

        services.Replace(ServiceDescriptor.Singleton<IClientCache>(
            sp => new SqliteClientCache(sp.GetRequiredService<SqliteClientStorage>())));

        services.Replace(ServiceDescriptor.Singleton<IOutbox>(sp =>
        {
            var outbox = sp.GetRequiredService<SqliteOutbox>();
            outbox.SetFailureLog(sp.GetRequiredService<SqliteSyncFailureLog>());
            return outbox;
        }));

        services.Replace(ServiceDescriptor.Singleton<ISyncFailureLog>(sp =>
        {
            var log = sp.GetRequiredService<SqliteSyncFailureLog>();
            log.SetOutbox(sp.GetRequiredService<SqliteOutbox>());
            return log;
        }));

        services.Replace(ServiceDescriptor.Singleton<IWritableSyncFailureLog>(
            sp => sp.GetRequiredService<SqliteSyncFailureLog>()));

        return services;
    }
}
