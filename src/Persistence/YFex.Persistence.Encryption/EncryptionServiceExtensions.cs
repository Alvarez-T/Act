using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YFex.Cryptography;
using YFex.Cryptography.Windows;
using YFex.Persistence;

namespace YFex.Persistence.Encryption;

/// <summary>Options of <see cref="EncryptionServiceExtensions.AddYFexStorageEncryption(IServiceCollection, Action{StorageEncryptionOptions}?)"/>.</summary>
public sealed class StorageEncryptionOptions
{
    internal byte[]? ProvidedKey;

    /// <summary>Bound into every sealed value, so values cannot be moved between stores.</summary>
    public string StoreName { get; set; } = EncryptedKeyValueStore.DefaultStoreName;

    /// <summary>Key ring of the storage key (default <c>"device/storage"</c>). Configured here unless the app configured it already.</summary>
    public string KeyRing { get; set; } = EncryptedKeyValueStore.DefaultKeyRing;

    /// <summary>
    /// The app's name for its keys: the default CNG root key is <c>"YFex.RootKey." + KeyName</c>, key rings live in
    /// <c>{KeyDirectory}\{KeyName}\</c>, and offline Phase 0's wrapped file <c>{KeyDirectory}\{KeyName}.storage-key</c> (CNG key
    /// <c>"YFex.Persistence.StorageKey." + KeyName</c>) is imported from. Default: the entry assembly name.
    /// </summary>
    public string KeyName { get; set; } = Assembly.GetEntryAssembly()?.GetName().Name ?? "default";

    /// <summary>
    /// Directory of the key rings. Default: <c>%LOCALAPPDATA%\YFex\Keys</c>, or <c>%PROGRAMDATA%\YFex\Keys</c> with
    /// <see cref="MachineScope"/>.
    /// </summary>
    public string? KeyDirectory { get; set; }

    /// <summary>Machine-wide root key (Windows CNG machine key + ProgramData), for a Windows service or several accounts sharing one store.</summary>
    public bool MachineScope { get; set; }

    /// <summary>Try the TPM first (Windows). The software key storage provider is used when there is no usable TPM.</summary>
    public bool PreferHardwareKey { get; set; } = true;

    /// <summary>Rotation of the storage key (default yearly). Old keys keep opening their values.</summary>
    public TimeSpan RotateEvery { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Import offline Phase 0's wrapped storage-key file into the new ring once, so its values stay readable (default on).</summary>
    public bool ImportLegacyStorageKey { get; set; } = true;

    /// <summary>
    /// Use a 32-byte key supplied by the app instead of a key ring (kept in memory only; rotation is the app's job). Values of
    /// earlier versions written with the same key stay readable.
    /// </summary>
    public StorageEncryptionOptions UseKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32) throw new ArgumentException("A storage key has exactly 32 bytes.", nameof(key));
        ProvidedKey = key;
        return this;
    }

    internal string Directory() => KeyDirectory ?? Path.Combine(
        Environment.GetFolderPath(MachineScope ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
        "YFex", "Keys");

    internal string LegacyKeyFilePath() => Path.Combine(Directory(), KeyName + ".storage-key");
}

/// <summary>Where the AES-256 storage key comes from (before YFex.Cryptography).</summary>
[Obsolete("Use AddYFexStorageEncryption(storage => …): the default is the OS key store; storage.UseKey(bytes) supplies a key.")]
public enum EncryptionKeySource
{
    /// <summary>A key ring wrapped by the platform (CNG/TPM on Windows).</summary>
    OsKeyStore,

    /// <summary>Caller supplies a 32-byte key.</summary>
    Provided,
}

public static class EncryptionServiceExtensions
{
    private const string InnerStoreKey = "yfex.storage.inner";
    private const byte LegacyKeyFileVersion = 0x01;

    /// <summary>
    /// Decorates the currently registered <see cref="IKeyValueStore"/> with encryption at rest (cryptography 09 §2). Call AFTER
    /// registering the underlying store (memory / SQLite / IndexedDb).
    /// <code>
    /// services.AddYFexSqliteStorage(AppPaths.Data("farma.db"));
    /// services.AddYFexStorageEncryption(storage => storage.StoreName = "farma");
    /// </code>
    /// Unless the app configured YFex.Cryptography itself (<c>AddYFexCryptography</c>), the root key is a non-exportable CNG key
    /// (TPM first) on Windows, or a 0600 key file elsewhere (with a warning: protected by file permissions only), and key rings are
    /// files under <see cref="StorageEncryptionOptions.KeyDirectory"/>.
    /// </summary>
    public static IServiceCollection AddYFexStorageEncryption(this IServiceCollection services, Action<StorageEncryptionOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new StorageEncryptionOptions();
        configure?.Invoke(options);
        var ringName = new KeyRingName(options.KeyRing);

        if (options.ProvidedKey is { } provided)
        {
            var key = SecretKey.Import(EncryptedKeyValueStore.LegacyStorageKeyId, KeyPurpose.Sealing, provided);
            var ring = YFex.Cryptography.KeyRing.FromKey(ringName, key);
            Decorate(services, (sp, inner) => new EncryptedKeyValueStore(inner, ring, options.StoreName, Logger(sp)));
            return services;
        }

        CryptographyBuilder builder = CryptographyServiceCollectionExtensions.BuilderOf(services);
        if (!builder.HasRootKey)
        {
            if (OperatingSystem.IsWindows())
                builder.UseCngRootKey("YFex.RootKey." + options.KeyName, options.MachineScope, options.PreferHardwareKey);
            else
                builder.UseFileRootKey(Path.Combine(options.Directory(), options.KeyName + ".root-key"));
        }
        if (!builder.HasStore) builder.StoreKeyRingsInFiles(Path.Combine(options.Directory(), options.KeyName));
        builder.KeyRing(options.KeyRing, ring =>
        {
            ring.For(KeyPurpose.Sealing).RotateEvery(options.RotateEvery);
            if (options.ImportLegacyStorageKey) ring.ImportInitialKey((_, ct) => ImportLegacyStorageKeyAsync(options, ct));
        });

        Decorate(services, (sp, inner) =>
        {
            ILogger? logger = Logger(sp);
            if (!OperatingSystem.IsWindows() && logger is not null)
                logger.LogWarning("Storage encryption on this platform keeps its root key in a file protected only by file permissions " +
                                  "({Directory}). Use AddYFexCryptography(…) with an OS key store or an environment root key in production.",
                                  options.Directory());
            return new EncryptedKeyValueStore(inner, sp.GetRequiredService<KeyRingProvider>(), ringName, options.StoreName, logger);
        });
        return services;
    }

    /// <summary>Before YFex.Cryptography: kept for one release. <see cref="EncryptionKeySource.Provided"/> = <c>storage.UseKey(key)</c>.</summary>
    [Obsolete("Use AddYFexStorageEncryption(storage => …): the default is the OS key store; storage.UseKey(bytes) supplies a key.")]
    public static IServiceCollection AddYFexStorageEncryption(this IServiceCollection services, EncryptionKeySource keySource,
        byte[]? providedKey = null, Action<StorageEncryptionOptions>? configure = null)
        => services.AddYFexStorageEncryption(options =>
        {
            configure?.Invoke(options);
            if (keySource == EncryptionKeySource.Provided)
                options.UseKey(providedKey ?? throw new ArgumentException("Provided key must be exactly 32 bytes for AES-256.", nameof(providedKey)));
        });

    /// <summary>
    /// Offline Phase 0 kept the storage key in <c>{KeyName}.storage-key</c>: <c>[0x01][provider byte][RSA-OAEP ciphertext]</c>, wrapped by
    /// the CNG key <c>"YFex.Persistence.StorageKey." + KeyName</c>. Imported once as the first key of the ring, so its values stay
    /// readable; the file is left in place (support can still see it) and never read again once the ring exists.
    /// </summary>
    private static async ValueTask<SecretKey?> ImportLegacyStorageKeyAsync(StorageEncryptionOptions options, CancellationToken cancellationToken)
    {
        string path = options.LegacyKeyFilePath();
        if (!File.Exists(path)) return null;
        byte[] file = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (file.Length < 3 || file[0] != LegacyKeyFileVersion)
            throw new InvalidOperationException($"'{path}' is not a storage-key file of YFex.Persistence.Encryption (unknown format).");

        if (file[1] is RootKeyFormat.CngPlatform or RootKeyFormat.CngSoftware)
        {
            if (!OperatingSystem.IsWindows())
                throw new InvalidOperationException($"'{path}' was wrapped by a Windows CNG key and cannot be opened on this platform.");
            var legacyRoot = new CngRootKeyProvider("YFex.Persistence.StorageKey." + options.KeyName, options.MachineScope, options.PreferHardwareKey);
            byte[] material = GC.AllocateArray<byte>(64, pinned: true);
            try
            {
                int length = await legacyRoot.UnwrapAsync(file.AsMemory(1), material, cancellationToken).ConfigureAwait(false);
                if (length != 32) throw new InvalidOperationException($"The storage key in '{path}' has {length} bytes instead of 32.");
                return SecretKey.Import(EncryptedKeyValueStore.LegacyStorageKeyId, KeyPurpose.Sealing, material.AsSpan(0, length));
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"The storage key in '{path}' cannot be recovered ({ex.Message}). Run as the account and on the machine that created it; " +
                    "to start over (losing the values encrypted with it), set ImportLegacyStorageKey = false.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        throw new InvalidOperationException(
            $"'{path}' was wrapped by ASP.NET Core DataProtection (offline Phase 0 on Linux/macOS), which this version no longer reads " +
            "(no DataProtection dependency, cryptography ⚑K7). Read the values with the previous version, or set ImportLegacyStorageKey = false " +
            "to start over.");
    }

    private static ILogger? Logger(IServiceProvider services)
        => (services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)?.CreateLogger<EncryptedKeyValueStore>();

    /// <summary>
    /// Moves the previously registered store to a keyed registration (no reflection: the container builds it as before) and
    /// registers the decorator in its place (cryptography C8).
    /// </summary>
    private static void Decorate(IServiceCollection services, Func<IServiceProvider, IKeyValueStore, EncryptedKeyValueStore> create)
    {
        ServiceDescriptor inner = services.LastOrDefault(d => d.ServiceType == typeof(IKeyValueStore) && !d.IsKeyedService)
            ?? throw new InvalidOperationException("No IKeyValueStore registered. Register a storage backend before AddYFexStorageEncryption.");
        services.Remove(inner);
        services.Add(Keyed(inner));
        services.AddSingleton<IKeyValueStore>(sp => create(sp, sp.GetRequiredKeyedService<IKeyValueStore>(InnerStoreKey)));
    }

    private static ServiceDescriptor Keyed(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is { } instance)
            return new ServiceDescriptor(typeof(IKeyValueStore), InnerStoreKey, instance);
        if (descriptor.ImplementationFactory is { } factory)
            return new ServiceDescriptor(typeof(IKeyValueStore), InnerStoreKey, (sp, _) => factory(sp), descriptor.Lifetime);
        return new ServiceDescriptor(typeof(IKeyValueStore), InnerStoreKey, descriptor.ImplementationType!, descriptor.Lifetime);
    }
}
