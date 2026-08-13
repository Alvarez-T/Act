using YFex.Persistence;

namespace YFex.Persistence.Encryption;

/// <summary>
/// <see cref="IKeyValueStore"/> decorator that transparently encrypts stored values with
/// <see cref="IValueProtector"/>. Keys stay plaintext to preserve prefix enumeration. A value
/// stored unencrypted (or with an unknown version tag) reads back as <see langword="null"/>
/// rather than corrupt data. Formerly <c>EncryptedClientStorage</c>.
/// </summary>
public sealed class EncryptedKeyValueStore : IKeyValueStore
{
    private readonly IKeyValueStore _inner;
    private readonly IValueProtector _protector;

    public EncryptedKeyValueStore(IKeyValueStore inner, IValueProtector protector)
    {
        _inner = inner;
        _protector = protector;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var ciphertext = await _inner.GetAsync(key, ct).ConfigureAwait(false);
        if (ciphertext is null) return null;
        return _protector.Unprotect(ciphertext);
    }

    public ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
        => _inner.SetAsync(key, _protector.Protect(value), ttl, ct);

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
        => _inner.DeleteAsync(key, ct);

    public async ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => await GetAsync(key, ct).ConfigureAwait(false) is not null;

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _inner.GetKeysWithPrefixAsync(prefix, ct);

    public ValueTask ClearAsync(CancellationToken ct = default)
        => _inner.ClearAsync(ct);
}
