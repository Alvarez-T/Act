using YFex.Messaging.Rpc;

namespace YFex.Messaging.Rpc.Encryption;

/// <summary>
/// <see cref="IClientStorage"/> decorator that transparently encrypts all stored values
/// with <see cref="IValueProtector"/>. Keys are stored in plaintext to preserve indexability.
///
/// Wrapping order: <c>GetAsync</c> → decrypt → return; <c>SetAsync</c> → encrypt → store.
/// A blob stored without encryption cannot be read back (returns <c>null</c> with a clear error
/// when the version tag is invalid). A version mismatch (e.g. switching from encrypted to plain)
/// returns <c>null</c> rather than silently returning corrupt data.
/// </summary>
public sealed class EncryptedClientStorage : IClientStorage
{
    private readonly IClientStorage _inner;
    private readonly IValueProtector _protector;

    public EncryptedClientStorage(IClientStorage inner, IValueProtector protector)
    {
        _inner = inner;
        _protector = protector;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var ciphertext = await _inner.GetAsync(key, ct);
        if (ciphertext is null) return null;
        return _protector.Unprotect(ciphertext);
    }

    public async ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var ciphertext = _protector.Protect(value);
        await _inner.SetAsync(key, ciphertext, ttl, ct);
    }

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
        => _inner.DeleteAsync(key, ct);

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _inner.GetKeysWithPrefixAsync(prefix, ct);

    public ValueTask ClearAsync(CancellationToken ct = default)
        => _inner.ClearAsync(ct);
}
