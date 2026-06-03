using System.Text;
using YFex.Messaging.Rpc;
using YFex.Messaging.Rpc.Encryption;

namespace YFex.Messaging.Tests.Encryption;

/// <summary>Tests #32–35: AES-GCM encryption opt-in/out, AEAD tamper detection, and key derivation.</summary>
[Trait("Category", "Encryption")]
public sealed class EncryptionTests
{
    // 32-byte key for AES-256 — deterministic for tests
    private static readonly byte[] Key1 = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Key2 = Enumerable.Range(32, 32).Select(i => (byte)(i & 0xFF)).ToArray();

    private static AesGcmValueProtector Protector(byte[] key) =>
        new AesGcmValueProtector(new ProvidedKeyProvider(key));

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ── Test #32: Encryption opt-in ───────────────────────────────────────────

    [Fact]
    public async Task EncryptedStorage_GetAsync_ReturnsOriginalPlaintext()
    {
        var inner = new InMemoryClientStorage();
        using var protector = Protector(Key1);
        var encrypted = new EncryptedClientStorage(inner, protector);

        await encrypted.SetAsync("k", Bytes("secret"));
        var result = await encrypted.GetAsync("k");

        result.Should().Equal(Bytes("secret"),
            "value stored with encryption must decrypt back to the original bytes");
    }

    [Fact]
    public async Task EncryptedStorage_StoredBytesAreDifferentFromPlaintext()
    {
        var inner = new InMemoryClientStorage();
        using var protector = Protector(Key1);
        var encrypted = new EncryptedClientStorage(inner, protector);
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
        var inner = new InMemoryClientStorage();
        using var protector = Protector(Key1);

        // Write WITHOUT encryption (direct to inner)
        await inner.SetAsync("k", Bytes("unencrypted-value"));

        // Read WITH encryption — version byte is wrong, so Unprotect returns null
        var encrypted = new EncryptedClientStorage(inner, protector);
        var result = await encrypted.GetAsync("k");

        result.Should().BeNull(
            "a value stored without encryption must not be readable through the encrypted decorator");
    }

    [Fact]
    public async Task EncryptedValue_IsUnreadable_ThroughPlaintextStorage()
    {
        var inner = new InMemoryClientStorage();
        using var protector = Protector(Key1);
        var encrypted = new EncryptedClientStorage(inner, protector);

        await encrypted.SetAsync("k", Bytes("secret"));
        var raw = await inner.GetAsync("k");

        // The raw bytes start with version byte 0x01 — they are NOT valid UTF-8 meaningful data
        raw.Should().NotEqual(Bytes("secret"),
            "encrypted bytes differ from the original plaintext");
    }

    // ── Test #34: AEAD tamper detection ──────────────────────────────────────

    [Fact]
    public async Task TamperedCiphertext_ReturnsNull_NotGarbage()
    {
        var inner = new InMemoryClientStorage();
        using var protector = Protector(Key1);
        var encrypted = new EncryptedClientStorage(inner, protector);

        await encrypted.SetAsync("k", Bytes("original"));
        var raw = await inner.GetAsync("k");

        // Flip a byte in the ciphertext body (not the nonce or tag — those also cause failure,
        // but flipping the ciphertext body is the classic tamper scenario)
        var tampered = raw!.ToArray();
        tampered[15] ^= 0xFF; // flip bits in ciphertext region
        await inner.SetAsync("k", tampered);

        var result = await encrypted.GetAsync("k");
        result.Should().BeNull(
            "AEAD tag mismatch must return null, not silently return corrupt plaintext");
    }

    // ── Test #35: Different keys produce different ciphertext / unreadable result ─

    [Fact]
    public async Task ValueWrittenWithKey1_IsNotReadable_WithKey2()
    {
        var inner = new InMemoryClientStorage();
        using var prot1 = Protector(Key1);
        using var prot2 = Protector(Key2);
        var enc1 = new EncryptedClientStorage(inner, prot1);
        var enc2 = new EncryptedClientStorage(inner, prot2);

        await enc1.SetAsync("k", Bytes("key1-value"));
        var result = await enc2.GetAsync("k");

        result.Should().BeNull(
            "value encrypted with Key1 cannot be decrypted with Key2 — AEAD authentication fails");
    }

    // ── AesGcmValueProtector unit tests ───────────────────────────────────────

    [Fact]
    public void Protector_RoundTrip_ReturnsOriginalBytes()
    {
        using var protector = Protector(Key1);
        var plaintext = Bytes("round-trip-test");

        var ciphertext = protector.Protect(plaintext);
        var decrypted = protector.Unprotect(ciphertext);

        decrypted.Should().Equal(plaintext);
    }

    [Fact]
    public void Protector_DifferentNonces_ProduceDifferentCiphertext()
    {
        using var protector = Protector(Key1);
        var plaintext = Bytes("same-input");

        var c1 = protector.Protect(plaintext);
        var c2 = protector.Protect(plaintext);

        c1.Should().NotEqual(c2,
            "each call must use a random nonce, producing distinct ciphertexts");
    }

    [Fact]
    public void Protector_EmptyInput_IsHandledCorrectly()
    {
        using var protector = Protector(Key1);
        var empty = Array.Empty<byte>();

        var ciphertext = protector.Protect(empty);
        var decrypted = protector.Unprotect(ciphertext);

        decrypted.Should().Equal(empty);
    }

    [Fact]
    public void ProvidedKeyProvider_WrongKeyLength_Throws()
    {
        var shortKey = new byte[16]; // AES-256 requires 32 bytes
        var act = () => new ProvidedKeyProvider(shortKey);
        act.Should().Throw<ArgumentException>("key must be exactly 32 bytes");
    }
}
