namespace YFex.Persistence;

/// <summary>
/// The unified opaque key→bytes store: the single durable substrate for local persistence.
/// Callers hand it a payload and get the same payload back by key — it never inspects the
/// contents. This is the superset that replaces the former <c>ISnapshotStore</c> (async
/// snapshots) and <c>IClientStorage</c> (messaging cache/outbox backing).
/// </summary>
/// <remarks>
/// Backends: <see cref="MemoryKeyValueStore"/> (core), <c>FileSystemKeyValueStore</c>
/// (YFex.Persistence.FileSystem), <c>SqliteKeyValueStore</c> (YFex.Persistence.Sqlite),
/// <c>IndexedDbKeyValueStore</c> (YFex.Persistence.IndexedDb), plus the
/// <c>EncryptedKeyValueStore</c> decorator (YFex.Persistence.Encryption).
/// </remarks>
public interface IKeyValueStore
{
    /// <summary>Returns the bytes stored under <paramref name="key"/>, or <see langword="null"/> if absent/expired.</summary>
    ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Writes <paramref name="value"/> under <paramref name="key"/>, optionally expiring after <paramref name="ttl"/>.</summary>
    ValueTask SetAsync(string key, byte[] value, TimeSpan? ttl = null, CancellationToken ct = default);

    /// <summary>Removes the entry for <paramref name="key"/>. No-op when not found.</summary>
    ValueTask DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Returns <see langword="true"/> when a non-expired value exists for <paramref name="key"/>.</summary>
    ValueTask<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Returns all keys beginning with <paramref name="prefix"/> (used for batch operations).</summary>
    ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default);

    /// <summary>Removes every entry.</summary>
    ValueTask ClearAsync(CancellationToken ct = default);
}
