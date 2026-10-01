namespace YFex.Persistence.Tenancy;

/// <summary>
/// <see cref="IKeyValueStore"/> decorator that partitions every key by the scope in effect, so tenants
/// (and, if configured, users) sharing one device or one backend never see each other's data. Callers
/// keep using plain logical keys — the scope is read implicitly from <c>IScopeContext</c>, or explicitly
/// from a <c>ScopeOverride</c>.
/// </summary>
/// <remarks>
/// <see cref="ClearAsync"/> clears only the current partition (or, when unscoped, only the shared area) —
/// never other tenants' data. To wipe everything, clear the undecorated backend.
/// </remarks>
public sealed class ScopedKeyValueStore : IKeyValueStore
{
    private readonly IKeyValueStore _inner;
    private readonly PersistenceKeyScoper _scoper;

    public ScopedKeyValueStore(IKeyValueStore inner, PersistenceKeyScoper scoper)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _scoper = scoper ?? throw new ArgumentNullException(nameof(scoper));
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
        => _inner.GetAsync(_scoper.Map(key), ct);

    public ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default)
        => _inner.SetAsync(_scoper.Map(key), value, ttl, ct);

    public ValueTask DeleteAsync(string key, CancellationToken ct = default)
        => _inner.DeleteAsync(_scoper.Map(key), ct);

    public ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default)
        => _inner.ExistsAsync(_scoper.Map(key), ct);

    public async ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
    {
        var partition = _scoper.PrefixFor(prefix);
        var physical = await _inner.GetKeysWithPrefixAsync(partition + prefix, ct).ConfigureAwait(false);
        return _scoper.Unmap(physical, partition);
    }

    public async ValueTask ClearAsync(CancellationToken ct = default)
    {
        var partition = _scoper.PrefixFor(string.Empty);
        var physical = await _inner.GetKeysWithPrefixAsync(partition, ct).ConfigureAwait(false);
        foreach (var key in physical)
        {
            if (partition.Length == 0 && _scoper.IsPartitioned(key)) continue; // shared clear spares tenants
            await _inner.DeleteAsync(key, ct).ConfigureAwait(false);
        }
    }
}

/// <summary><see cref="ISyncLocalStore"/> decorator with the same partitioning as <see cref="ScopedKeyValueStore"/>.</summary>
public sealed class ScopedSyncLocalStore : ISyncLocalStore
{
    private readonly ISyncLocalStore _inner;
    private readonly PersistenceKeyScoper _scoper;

    public ScopedSyncLocalStore(ISyncLocalStore inner, PersistenceKeyScoper scoper)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _scoper = scoper ?? throw new ArgumentNullException(nameof(scoper));
    }

    public byte[]? Read(string key) => _inner.Read(_scoper.Map(key));
    public void Write(string key, byte[] data) => _inner.Write(_scoper.Map(key), data);
    public void Delete(string key) => _inner.Delete(_scoper.Map(key));
    public bool Exists(string key) => _inner.Exists(_scoper.Map(key));
}
