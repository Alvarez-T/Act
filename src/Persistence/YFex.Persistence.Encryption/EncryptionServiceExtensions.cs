using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Persistence;

namespace YFex.Persistence.Encryption;

/// <summary>Where the AES-256 key comes from.</summary>
public enum EncryptionKeySource
{
    /// <summary>Key derived from the OS key store via Microsoft.AspNetCore.DataProtection.</summary>
    OsKeyStore,
    /// <summary>Caller supplies a 32-byte key.</summary>
    Provided,
}

public static class EncryptionServiceExtensions
{
    /// <summary>
    /// Decorates the currently registered <see cref="IKeyValueStore"/> with AES-GCM encryption.
    /// Call AFTER registering the underlying store (memory / SQLite / IndexedDb).
    /// </summary>
    public static IServiceCollection AddYFexStorageEncryption(
        this IServiceCollection services,
        EncryptionKeySource keySource = EncryptionKeySource.OsKeyStore,
        byte[]? providedKey = null)
    {
        switch (keySource)
        {
            case EncryptionKeySource.OsKeyStore:
                services.TryAddSingleton<IKeyProvider>(sp =>
                    new DataProtectionKeyProvider(sp.GetRequiredService<IDataProtectionProvider>()));
                break;
            case EncryptionKeySource.Provided:
                if (providedKey is null || providedKey.Length != 32)
                    throw new ArgumentException("Provided key must be exactly 32 bytes for AES-256.", nameof(providedKey));
                services.TryAddSingleton<IKeyProvider>(new ProvidedKeyProvider(providedKey));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(keySource));
        }

        services.TryAddSingleton<IValueProtector, AesGcmValueProtector>();

        // Decorate the previously-registered IKeyValueStore.
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(IKeyValueStore))
            ?? throw new InvalidOperationException(
                "No IKeyValueStore registered. Register a storage backend before AddYFexStorageEncryption.");
        services.Remove(inner);
        services.AddSingleton<IKeyValueStore>(sp => new EncryptedKeyValueStore(
            ResolveInner(sp, inner),
            sp.GetRequiredService<IValueProtector>()));

        return services;
    }

    private static IKeyValueStore ResolveInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IKeyValueStore instance) return instance;
        if (descriptor.ImplementationFactory is { } factory) return (IKeyValueStore)factory(sp);
        if (descriptor.ImplementationType is { } type) return (IKeyValueStore)ActivatorUtilities.CreateInstance(sp, type);
        throw new InvalidOperationException("Unable to resolve the inner IKeyValueStore to decorate.");
    }
}
