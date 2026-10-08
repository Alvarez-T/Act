using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cryptography;
using YFex.Cryptography.Windows;
using YFex.Persistence.Encryption;

namespace YFex.Persistence.Encryption.Tests;

/// <summary>
/// Offline F1 through YFex.Cryptography: the storage key is the same after a restart, and the key offline Phase 0 kept in its
/// wrapped file is imported once, so values written before this version stay readable. "Restart" = a new service provider over
/// the same key directory (CNG keys and key-ring files are OS/file state, not process state).
/// </summary>
public sealed class StorageKeyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yfex-storage-key-tests", Guid.NewGuid().ToString("N"));
    private readonly string _keyName = "YFex.Persistence.Encryption.Tests." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            new CngRootKeyProvider("YFex.RootKey." + _keyName).DeleteKey();
            new CngRootKeyProvider("YFex.Persistence.StorageKey." + _keyName).DeleteKey();
        }
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private ServiceProvider Build(MemoryKeyValueStore inner, Action<StorageEncryptionOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKeyValueStore>(inner);
        services.AddYFexStorageEncryption(options =>
        {
            options.KeyDirectory = _directory;
            options.KeyName = _keyName;
            options.PreferHardwareKey = false;   // keep the TPM out of unit tests (it is covered in YFex.Cryptography.Windows.Tests)
            configure?.Invoke(options);
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Encrypted_store_reads_after_a_restart()
    {
        var inner = new MemoryKeyValueStore();
        await using (ServiceProvider first = Build(inner))
            await first.GetRequiredService<IKeyValueStore>().SetAsync("outbox:0001", Encoding.UTF8.GetBytes("venda 1"));

        await using ServiceProvider second = Build(inner);
        (await second.GetRequiredService<IKeyValueStore>().GetAsync("outbox:0001")).Should().Equal(Encoding.UTF8.GetBytes("venda 1"));
        Directory.GetFiles(Path.Combine(_directory, _keyName), "*.keyring").Should().ContainSingle();
    }

    [Fact]
    public async Task Offline_phase_0_storage_key_is_imported_once_and_its_values_stay_readable()
    {
        if (!OperatingSystem.IsWindows()) return;

        // What offline Phase 0 left on a terminal: {KeyName}.storage-key = [0x01][CNG blob] and values in layout 0x02.
        byte[] storageKey = RandomNumberGenerator.GetBytes(32);
        var legacyWrapper = new CngRootKeyProvider("YFex.Persistence.StorageKey." + _keyName, preferHardware: false);
        byte[] blob = await legacyWrapper.WrapAsync(storageKey);
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, _keyName + ".storage-key"), [0x01, .. blob]);

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = Encoding.UTF8.GetBytes("venda feita antes da atualização");
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(storageKey, 16))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes("yfex.store|default|outbox:0007"));
        var inner = new MemoryKeyValueStore();
        await inner.SetAsync("outbox:0007", [0x02, .. nonce, .. ciphertext, .. tag]);

        await using (ServiceProvider provider = Build(inner))
        {
            var store = (EncryptedKeyValueStore)provider.GetRequiredService<IKeyValueStore>();
            (await store.GetAsync("outbox:0007")).Should().Equal(plaintext);
            KeyRing ring = await provider.GetRequiredService<KeyRingProvider>().GetAsync(new KeyRingName("device/storage"));
            ring.Current.Id.Should().Be(EncryptedKeyValueStore.LegacyStorageKeyId, "the old storage key keeps sealing until the yearly rotation");
        }

        // After the import the ring is the source of truth: the old file is no longer needed (and is left in place).
        File.Exists(Path.Combine(_directory, _keyName + ".storage-key")).Should().BeTrue();
        await using (ServiceProvider restarted = Build(inner, o => o.ImportLegacyStorageKey = false))
            (await restarted.GetRequiredService<IKeyValueStore>().GetAsync("outbox:0007")).Should().Equal(plaintext);
    }

    [Fact]
    public async Task Unrecoverable_legacy_key_file_fails_loudly_instead_of_generating_a_new_key()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, _keyName + ".storage-key"), [0x01, 0x03, 0xAA, 0xBB]);   // DataProtection-wrapped
        await using ServiceProvider provider = Build(new MemoryKeyValueStore());
        await FluentActions.Awaiting(() => provider.GetRequiredService<IKeyValueStore>().SetAsync("k", [1]).AsTask())
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*DataProtection*");
    }

    [Fact]
    public async Task Supplied_key_reads_values_of_earlier_versions_written_with_it()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] ciphertext = new byte[3];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, (byte[])[1, 2, 3], ciphertext, tag, Encoding.UTF8.GetBytes("yfex.store|default|k"));
        var inner = new MemoryKeyValueStore();
        await inner.SetAsync("k", [0x02, .. nonce, .. ciphertext, .. tag]);

        var services = new ServiceCollection();
        services.AddSingleton<IKeyValueStore>(inner);
        services.AddYFexStorageEncryption(storage => storage.UseKey(key));
        await using ServiceProvider provider = services.BuildServiceProvider();
        (await provider.GetRequiredService<IKeyValueStore>().GetAsync("k")).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Obsolete_overload_still_works_for_one_release()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKeyValueStore>(new MemoryKeyValueStore());
#pragma warning disable CS0618
        services.AddYFexStorageEncryption(EncryptionKeySource.Provided, RandomNumberGenerator.GetBytes(32));
#pragma warning restore CS0618
        await using ServiceProvider provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IKeyValueStore>();
        await store.SetAsync("k", [7]);
        (await store.GetAsync("k")).Should().Equal(7);
    }

    [Fact]
    public async Task Inner_store_registered_by_type_is_built_by_the_container_without_reflection_helpers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IKeyValueStore, MemoryKeyValueStore>();
        services.AddYFexStorageEncryption(storage => storage.UseKey(RandomNumberGenerator.GetBytes(32)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IKeyValueStore>();
        store.Should().BeOfType<EncryptedKeyValueStore>();
        await store.SetAsync("k", [9]);
        (await provider.GetRequiredKeyedService<IKeyValueStore>("yfex.storage.inner").GetAsync("k"))![0].Should().Be(SealedValue.FormatVersion);
    }
}
