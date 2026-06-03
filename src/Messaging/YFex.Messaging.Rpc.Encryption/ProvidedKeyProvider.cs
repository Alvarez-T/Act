namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// <see cref="IKeyProvider"/> that returns a caller-supplied raw key.
/// Suitable for testing or build-time configured secrets.
/// The key must be exactly 32 bytes.
/// </summary>
public sealed class ProvidedKeyProvider : IKeyProvider
{
    private readonly byte[] _key;

    public ProvidedKeyProvider(byte[] key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Key must be exactly 32 bytes for AES-256.", nameof(key));
        _key = key;
    }

    public byte[] GetKey() => _key;
}
