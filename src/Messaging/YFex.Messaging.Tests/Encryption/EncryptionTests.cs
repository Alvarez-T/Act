using System.Text;
using YFex.Cryptography;
using YFex.Messaging.Rpc;
using YFex.Persistence.Encryption;

namespace YFex.Messaging.Tests.Encryption;

/// <summary>
/// Tests #32–35: AES-GCM encryption opt-in/out, AEAD tamper detection, keys. Since cryptography Phase 0 the decorator seals with
/// YFex.Cryptography, and a value that cannot be opened throws <see cref="StorageIntegrityException"/> instead of reading as null.
/// </summary>
[Trait("Category", "Encryption")]
public sealed class EncryptionTests
{
    // 32-byte key for AES-256 — deterministic for tests
    private static readonly byte[] Key1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Key2 = Enumerable.Range(32, 32).Select(i => (byte)(i & 0xFF)).ToArray();

    private static KeyRing Protector(byte[] key) =>
        KeyRing.FromKey(new KeyRingName("tests/storage"), SecretKey.Import(KeyId.New(), KeyPurpose.Sealing, key));

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ── Test #32: Encryption opt-in ───────────────────────────────────────────

    [Fact]
    public async Task EncryptedStorage_GetAsync_ReturnsOriginalPlaintext()
    {
        var inner = new MemoryKeyValueStore();
        var protector = Protector(Key1);
        var encrypted = new EncryptedKeyValueStore(inner, protector);

        await encrypted.SetAsync("k", Bytes("secret"));
        var result = await encrypted.GetAsync("k");

        result.Should().Equal(Bytes("secret"),
            "value stored with encryption must decrypt back to the original bytes");
    }

    [Fact]
    public async Task EncryptedStorage_StoredBytesAreDifferentFromPlaintext()
    {
        var inner = new MemoryKeyValueStore();
        var protector = Protector(Key1);
        var encrypted = new EncryptedKeyValueStore(inner, protector);
        var plaintext = Bytes("plaintext-value");

        await encrypted.SetAsync("raw", plaintext);
        var raw = await inner.GetAsync("raw"); // read the underlying bytes directly

        raw.Should().NotEqual(plaintext,
            "stored bytes must be ciphertext, not the original plaintext");
    }

    // ── Test #33: Encryption opt-out — plaintext unreadable via encrypted view ──

    [Fact]
    public async Task PlaintextValue_IsUnreadable_ThroughEncryptedStorage()
    {
        var inner = new MemoryKeyValueStore();
        var protector = Protector(Key1);

        // Write WITHOUT encryption (direct to inner)
        await inner.SetAsync("k", Bytes("unencrypted-value"));

        // Read WITH encryption — the version byte is wrong: reported, never returned as data
        var encrypted = new EncryptedKeyValueStore(inner, protector);
        (await encrypted.TryGetAsync("k")).Status.Should().Be(StoredValueStatus.UnsupportedFormat,
            "a value stored without encryption must not be readable through the encrypted decorator");
        await FluentActions.Awaiting(() => encrypted.GetAsync("k").AsTask()).Should().ThrowAsync<StorageIntegrityException>();
    }

    [Fact]
    public async Task EncryptedValue_IsUnreadable_ThroughPlaintextStorage()
    {
        var inner = new MemoryKeyValueStore();
        var protector = Protector(Key1);
        var encrypted = new EncryptedKeyValueStore(inner, protector);

        await encrypted.SetAsync("k", Bytes("secret"));
        var raw = await inner.GetAsync("k");

        // The raw bytes start with version byte 0x03 (sealed value v3) — they are NOT the plaintext
        raw.Should().NotEqual(Bytes("secret"),
            "encrypted bytes differ from the original plaintext");
    }

    // ── Test #34: AEAD tamper detection ──────────────────────────────────────

    [Fact]
    public async Task TamperedCiphertext_IsReported_NotGarbage()
    {
        var inner = new MemoryKeyValueStore();
        var protector = Protector(Key1);
        var encrypted = new EncryptedKeyValueStore(inner, protector);

        await encrypted.SetAsync("k", Bytes("original"));
        var raw = await inner.GetAsync("k");

        // Flip a byte in the ciphertext body (not the nonce or tag — those also cause failure,
        // but flipping the ciphertext body is the classic tamper scenario)
        var tampered = raw!.ToArray();
        tampered[35] ^= 0xFF; // flip bits in the ciphertext region (the header is 31 bytes)
        await inner.SetAsync("k", tampered);

        (await encrypted.TryGetAsync("k")).Status.Should().Be(StoredValueStatus.AuthenticationFailed,
            "AEAD tag mismatch must be reported, not silently return corrupt plaintext");
        await FluentActions.Awaiting(() => encrypted.GetAsync("k").AsTask()).Should().ThrowAsync<StorageIntegrityException>();
    }

    // ── Test #35: Different keys produce different ciphertext / unreadable result ─

    [Fact]
    public async Task ValueWrittenWithKey1_IsNotReadable_WithKey2()
    {
        var inner = new MemoryKeyValueStore();
        var prot1 = Protector(Key1);
        var prot2 = Protector(Key2);
        var enc1 = new EncryptedKeyValueStore(inner, prot1);
        var enc2 = new EncryptedKeyValueStore(inner, prot2);

        await enc1.SetAsync("k", Bytes("key1-value"));

        (await enc2.TryGetAsync("k")).Status.Should().Be(StoredValueStatus.KeyUnknown,
            "value encrypted with Key1 cannot be decrypted with Key2 — its key id is not in Key2's ring");
    }

    // ── Sealing unit tests (YFex.Cryptography) ─────────────────────────────────

    [Fact]
    public void Seal_RoundTrip_ReturnsOriginalBytes()
    {
        var ring = Protector(Key1);
        var plaintext = Bytes("round-trip-test");
        Span<byte> context = stackalloc byte[32];

        var sealedValue = SealedValue.Seal(ring, plaintext, SealContext.Write(context, EncryptedKeyValueStore.StoreDomain));
        var decrypted = new byte[SealedValue.GetPlaintextLength(sealedValue.Length)];
        SealedValue.Open(ring, sealedValue, SealContext.Write(context, EncryptedKeyValueStore.StoreDomain), decrypted).IsOpened.Should().BeTrue();

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void Seal_DifferentNonces_ProduceDifferentCiphertext()
    {
        var ring = Protector(Key1);
        var plaintext = Bytes("same-input");
        Span<byte> context = stackalloc byte[32];

        var c1 = SealedValue.Seal(ring, plaintext, SealContext.Write(context, EncryptedKeyValueStore.StoreDomain));
        var c2 = SealedValue.Seal(ring, plaintext, SealContext.Write(context, EncryptedKeyValueStore.StoreDomain));

        c1.Should().NotEqual(c2, "each call must use a random nonce, producing distinct ciphertexts");
    }

    [Fact]
    public async Task EmptyValue_IsHandledCorrectly()
    {
        var encrypted = new EncryptedKeyValueStore(new MemoryKeyValueStore(), Protector(Key1));
        await encrypted.SetAsync("empty", []);
        (await encrypted.GetAsync("empty")).Should().BeEmpty();
    }

    [Fact]
    public void SuppliedKey_WrongLength_Throws()
    {
        var shortKey = new byte[16]; // AES-256 requires 32 bytes
        var act = () => new StorageEncryptionOptions().UseKey(shortKey);
        act.Should().Throw<ArgumentException>("key must be exactly 32 bytes");
    }
}
