using System.Security.Cryptography;

namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// AES-256-GCM <see cref="IValueProtector"/>.
///
/// Blob layout (version 0x01):
/// <code>
/// [0]         Version byte  (0x01 = AesGcm-v1)
/// [1..12]     Nonce         (12 bytes, random)
/// [13..N-16]  Ciphertext    (same length as plaintext)
/// [N-16..N]   Auth tag      (16 bytes)
/// </code>
///
/// Keys are sourced from <see cref="IKeyProvider"/> and cached after the first call.
/// </summary>
public sealed class AesGcmValueProtector : IValueProtector, IDisposable
{
    private const byte Version = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 1 + NonceSize; // version + nonce

    private readonly IKeyProvider _keyProvider;
    private AesGcm? _aes;
    private bool _disposed;

    public AesGcmValueProtector(IKeyProvider keyProvider) => _keyProvider = keyProvider;

    private AesGcm Aes
    {
        get
        {
            if (_aes is not null) return _aes;
            var key = _keyProvider.GetKey();
            if (key.Length != 32)
                throw new InvalidOperationException($"AES-256 requires a 32-byte key; got {key.Length} bytes.");
            return _aes = new AesGcm(key, TagSize);
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var output = new byte[1 + NonceSize + plaintext.Length + TagSize];
        output[0] = Version;

        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = output.AsSpan(HeaderSize, plaintext.Length);
        var tag = output.AsSpan(HeaderSize + plaintext.Length, TagSize);

        Aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return output;
    }

    public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ciphertext.Length < 1 + NonceSize + TagSize) return null;
        if (ciphertext[0] != Version) return null; // unknown version — forward-compat skip

        var nonce = ciphertext.Slice(1, NonceSize);
        var plaintextLength = ciphertext.Length - HeaderSize - TagSize;
        var encryptedBytes = ciphertext.Slice(HeaderSize, plaintextLength);
        var tag = ciphertext.Slice(HeaderSize + plaintextLength, TagSize);

        var plaintext = new byte[plaintextLength];
        try
        {
            Aes.Decrypt(nonce, encryptedBytes, tag, plaintext);
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _aes?.Dispose();
    }
}
