using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Encryption;

public static class EncryptionMessagingExtensions
{
    /// <summary>
    /// Wraps the registered <see cref="IClientStorage"/> with AES-GCM encryption.
    ///
    /// Call this AFTER registering the underlying storage backend
    /// (e.g. <c>AddYFexSqliteStorage</c> or <c>AddYFexIndexedDBStorage</c>), which registers
    /// the raw storage under the keyed key <see cref="StorageServiceKeys.Inner"/>.
    ///
    /// Key source is selected via <paramref name="keySource"/>:
    /// <list type="bullet">
    ///   <item><see cref="EncryptionKeySource.OsKeyStore"/> — key derived from the OS key store via
    ///     <c>Microsoft.AspNetCore.DataProtection</c>.</item>
    ///   <item><see cref="EncryptionKeySource.Provided"/> — caller supplies a 32-byte key via
    ///     <paramref name="providedKey"/>.</item>
    /// </list>
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
                    throw new ArgumentException(
                        "Provided key must be exactly 32 bytes for AES-256.", nameof(providedKey));
                services.TryAddSingleton<IKeyProvider>(new ProvidedKeyProvider(providedKey));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(keySource));
        }

        services.TryAddSingleton<IValueProtector, AesGcmValueProtector>();

        // Replace IClientStorage with the encrypted decorator.
        // The inner (raw) storage is resolved via the keyed registration placed by the backend package
        // (StorageServiceKeys.Inner), which avoids recursive IClientStorage resolution.
        services.Replace(ServiceDescriptor.Singleton<IClientStorage>(sp =>
        {
            var inner = sp.GetRequiredKeyedService<IClientStorage>(StorageServiceKeys.Inner)
                ?? throw new InvalidOperationException(
                    $"No keyed IClientStorage registered under '{StorageServiceKeys.Inner}'. " +
                    "Call AddYFexSqliteStorage or AddYFexIndexedDBStorage before AddYFexStorageEncryption.");
            return new EncryptedClientStorage(inner, sp.GetRequiredService<IValueProtector>());
        }));

        return services;
    }
}
