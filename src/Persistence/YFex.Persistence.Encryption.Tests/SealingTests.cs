using System.Security.Cryptography;
using System.Text;
using YFex.Cryptography;
using YFex.Cryptography.Testing;
using YFex.Persistence.Encryption;

namespace YFex.Persistence.Encryption.Tests;

/// <summary>
/// The store decorator on YFex.Cryptography (cryptography Phase 0): values bound to store + key (offline F2), failures reported as
/// values or <see cref="StorageIntegrityException"/> (cryptography C10), earlier layouts still readable (golden files).
/// </summary>
public sealed class SealingTests
{
    private static readonly KeyRingName Ring = new(EncryptedKeyValueStore.DefaultKeyRing);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static KeyRing NewRing() => KeyRing.FromKey(Ring, SecretKey.Generate(KeyPurpose.Sealing));

    private static KeyRing LegacyRing(byte[] key)
        => KeyRing.FromKey(Ring, SecretKey.Import(EncryptedKeyValueStore.LegacyStorageKeyId, KeyPurpose.Sealing, key));

    [Fact]
    public async Task Round_trip_writes_sealed_value_v3()
    {
        var inner = new MemoryKeyValueStore();
        var store = new EncryptedKeyValueStore(inner, NewRing(), "farma");

        await store.SetAsync("outbox:0042", Bytes("venda 42"));

        (await store.GetAsync("outbox:0042")).Should().Equal(Bytes("venda 42"));
        byte[] raw = (await inner.GetAsync("outbox:0042"))!;
        raw[0].Should().Be(SealedValue.FormatVersion);
        raw.Length.Should().Be(SealedValue.GetSealedLength("venda 42".Length));
        (await store.TryGetAsync("outbox:0042")).Status.Should().Be(StoredValueStatus.Opened);
        (await store.GetAsync("absent")).Should().BeNull();
        (await store.TryGetAsync("absent")).Status.Should().Be(StoredValueStatus.Missing);
    }

    [Fact]
    public async Task Value_copied_under_another_key_or_store_is_reported_not_returned_as_missing()
    {
        var inner = new MemoryKeyValueStore();
        KeyRing ring = NewRing();
        var store = new EncryptedKeyValueStore(inner, ring, "farma");
        var other = new EncryptedKeyValueStore(inner, ring, "other");
        var failures = new List<StorageReadFailure>();
        store.ReadFailed += failures.Add;

        await store.SetAsync("outbox:0042", Bytes("venda 42"));
        await inner.SetAsync("outbox:0043", (await inner.GetAsync("outbox:0042"))!);

        (await store.TryGetAsync("outbox:0043")).Status.Should().Be(StoredValueStatus.AuthenticationFailed);
        await FluentActions.Awaiting(() => store.GetAsync("outbox:0043").AsTask())
            .Should().ThrowAsync<StorageIntegrityException>().Where(e => e.Status == StoredValueStatus.AuthenticationFailed && e.Key == "outbox:0043");
        await FluentActions.Awaiting(() => other.GetAsync("outbox:0042").AsTask()).Should().ThrowAsync<StorageIntegrityException>();
        (await store.ExistsAsync("outbox:0043")).Should().BeFalse();
        store.UnreadableValues.Should().BeGreaterThanOrEqualTo(2);
        failures.Should().Contain(new StorageReadFailure("farma", "outbox:0043", StoredValueStatus.AuthenticationFailed));
    }

    [Fact]
    public async Task Tampered_unencrypted_and_foreign_values_are_never_silent()
    {
        var inner = new MemoryKeyValueStore();
        var store = new EncryptedKeyValueStore(inner, NewRing());
        await store.SetAsync("k", Bytes("original"));
        byte[] tampered = (await inner.GetAsync("k"))!;
        tampered[^1] ^= 0xFF;
        await inner.SetAsync("k", tampered);
        await inner.SetAsync("plain", Bytes("unencrypted-value"));

        (await store.TryGetAsync("k")).Status.Should().Be(StoredValueStatus.AuthenticationFailed);
        (await store.TryGetAsync("plain")).Status.Should().Be(StoredValueStatus.UnsupportedFormat);

        var foreign = new EncryptedKeyValueStore(inner, NewRing());
        await store.SetAsync("k2", Bytes("x"));
        (await foreign.TryGetAsync("k2")).Status.Should().Be(StoredValueStatus.KeyUnknown);
    }

    [Fact]
    public async Task Golden_values_of_offline_phase_0_layout_0x02_open_and_are_resealed_on_write()
    {
        // Golden/persistence-v2.tsv was produced on 2026-10-04 by the interim AesGcmValueProtector, before this code existed.
        string[] lines = (await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Golden", "persistence-v2.tsv")))
            .Where(l => !l.StartsWith('#') && l.Length > 0).ToArray();
        lines.Should().HaveCount(4);
        KeyRing ring = LegacyRing(KnownAnswerVectors.GoldenKey());

        foreach (string line in lines)
        {
            string[] columns = line.Split('\t');
            var inner = new MemoryKeyValueStore();
            await inner.SetAsync(columns[1], Convert.FromHexString(columns[3]));
            var store = new EncryptedKeyValueStore(inner, ring, columns[0]);

            StoredValueResult read = await store.TryGetAsync(columns[1]);
            read.Status.Should().Be(StoredValueStatus.OpenedNeedsReseal, line);
            read.Value.Should().Equal(Convert.FromHexString(columns[2]));
            store.LegacyValuesRead.Should().Be(1);

            await store.SetAsync(columns[1], read.Value!);
            (await inner.GetAsync(columns[1]))![0].Should().Be(SealedValue.FormatVersion);
            (await store.GetAsync(columns[1])).Should().Equal(Convert.FromHexString(columns[2]));
        }
    }

    [Fact]
    public async Task Layout_0x01_without_associated_data_still_opens()
    {
        byte[] key = KnownAnswerVectors.GoldenKey();
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = Bytes("venda antiga");
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var inner = new MemoryKeyValueStore();
        await inner.SetAsync("old", [0x01, .. nonce, .. ciphertext, .. tag]);

        var store = new EncryptedKeyValueStore(inner, LegacyRing(key));
        (await store.GetAsync("old")).Should().Equal(plaintext);
        (await store.TryGetAsync("old")).Status.Should().Be(StoredValueStatus.OpenedNeedsReseal);
    }

    [Fact]
    public async Task Legacy_value_without_the_legacy_key_is_reported()
    {
        var inner = new MemoryKeyValueStore();
        await inner.SetAsync("old", Convert.FromHexString("029fd878bd0f14b57aaf0189fad5483198987aca6534a6d94b1f545e6440"));
        var store = new EncryptedKeyValueStore(inner, NewRing());
        (await store.TryGetAsync("old")).Status.Should().Be(StoredValueStatus.KeyUnknown);
    }

    [Fact]
    public async Task Value_of_a_destroyed_key_is_erased_and_reads_as_missing()
    {
        var rings = new KeyRingProvider(new InMemoryKeyRingStore(), new Dictionary<string, KeyRingOptions> { [Ring.Value] = new() });
        var inner = new MemoryKeyValueStore();
        var store = new EncryptedKeyValueStore(inner, rings, Ring);
        await store.SetAsync("cliente:4711", Bytes("Maria"));
        KeyId old = (await rings.GetAsync(Ring)).Current.Id;
        await rings.RotateAsync(Ring);
        await rings.DestroyAsync(Ring, old);

        (await store.TryGetAsync("cliente:4711")).Status.Should().Be(StoredValueStatus.Erased);
        (await store.GetAsync("cliente:4711")).Should().BeNull();
        store.UnreadableValues.Should().Be(0, "erasure is a normal outcome, not a failure");
    }

    [Fact]
    public async Task Another_installation_rotating_the_shared_ring_is_picked_up_by_reloading()
    {
        var shared = new InMemoryKeyRingStore();
        var options = new Dictionary<string, KeyRingOptions> { [Ring.Value] = new() };
        var nodeA = new KeyRingProvider(shared, options);
        var nodeB = new KeyRingProvider(shared, options);
        var inner = new MemoryKeyValueStore();
        var storeA = new EncryptedKeyValueStore(inner, nodeA, Ring);
        var storeB = new EncryptedKeyValueStore(inner, nodeB, Ring);
        await storeB.SetAsync("warm", Bytes("x"));   // node B caches the ring

        await nodeA.RotateAsync(Ring);
        await storeA.SetAsync("k", Bytes("sealed with A's new key"));

        (await storeB.GetAsync("k")).Should().Equal(Bytes("sealed with A's new key"));
    }

    [Fact]
    public async Task Concurrent_writes_and_reads_never_corrupt_values()
    {
        var inner = new MemoryKeyValueStore();
        var store = new EncryptedKeyValueStore(inner, NewRing());
        await Parallel.ForAsync(0, 16, async (worker, ct) =>
        {
            for (int i = 0; i < 300; i++)
            {
                string key = $"w{worker}:{i}";
                await store.SetAsync(key, Bytes(key), ct: ct);
                (await store.GetAsync(key, ct)).Should().Equal(Bytes(key));
            }
        });
        store.UnreadableValues.Should().Be(0);
    }
}
