using MemoryPack;

namespace YFex.Persistence;

/// <summary>
/// Cache entry wrapper: a schema stamp, the stored-at time, the serialized payload, and its stale
/// flag. <see cref="Schema"/> lets a deploy invalidate incompatible offline entries: on read, an
/// entry whose stamp differs from the cache's current schema is dropped instead of mis-deserialized.
/// New members are appended so MemoryPack reads pre-versioning bytes as <c>Schema = 0</c> (dropped).
/// </summary>
[MemoryPackable]
internal partial record CacheEnvelope(byte[] Payload, bool IsStale, int Schema, long StoredAtTicks);

/// <summary>
/// Backend-agnostic <see cref="ICache"/> implemented over an <see cref="IKeyValueStore"/>.
/// Works over any store (memory, SQLite, …) and supports TTL, stale marking, prefix enumeration,
/// provenance-aware reads, and schema versioning. Replaces the former <c>InMemoryClientCache</c>,
/// <c>SqliteClientCache</c>, and <c>StorageBackedClientCache</c>.
/// </summary>
public sealed class KeyValueCache : ICache
{
    private readonly IKeyValueStore _store;
    private readonly IPersistenceSerializer _serializer;
    private readonly int _schema;

    /// <param name="schema">
    /// Current payload schema stamp. Bump it (e.g. on a deploy that changes cached record shapes)
    /// to make previously stored entries read as a miss instead of deserializing corrupt data.
    /// </param>
    public KeyValueCache(IKeyValueStore store, IPersistenceSerializer? serializer = null, int schema = 1)
    {
        _store = store;
        _serializer = serializer ?? MemoryPackPersistenceSerializer.Instance;
        _schema = schema;
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var env = await ReadEnvelopeAsync(key, ct).ConfigureAwait(false);
        return env is null ? default : _serializer.Deserialize<T>(env.Payload);
    }

    public async ValueTask<CacheResult<T>> TryGetAsync<T>(string key, CancellationToken ct = default)
    {
        var env = await ReadEnvelopeAsync(key, ct).ConfigureAwait(false);
        if (env is null) return CacheResult<T>.Missing;

        var payload = _serializer.Deserialize<T>(env.Payload);
        if (payload is null) return CacheResult<T>.Missing;

        var storedAt = new DateTimeOffset(env.StoredAtTicks, TimeSpan.Zero);
        return env.IsStale ? CacheValue<T>.Stale(payload, storedAt) : CacheValue<T>.Fresh(payload, storedAt);
    }

    public ValueTask SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        var env = new CacheEnvelope(_serializer.Serialize(value), IsStale: false, _schema, DateTimeOffset.UtcNow.UtcTicks);
        return _store.SetAsync(key, MemoryPackSerializer.Serialize(env), ttl, ct);
    }

    /// <summary>Honors <see cref="CacheEntryOptions.Duration"/>; other options are ignored (durable store, no L1 bound).</summary>
    public ValueTask SetAsync<T>(string key, T value, CacheEntryOptions options, CancellationToken ct = default)
        => SetAsync(key, value, options.Duration, ct);

    public ValueTask InvalidateAsync(string key, CancellationToken ct = default)
        => _store.DeleteAsync(key, ct);

    public async ValueTask UpdateAsync<T>(string key, Func<T, T> mutator, CancellationToken ct = default)
    {
        var current = await GetAsync<T>(key, ct).ConfigureAwait(false);
        if (current is null) return;
        await SetAsync(key, mutator(current), ct: ct).ConfigureAwait(false);
    }

    public async ValueTask MarkStaleAsync(string key, CancellationToken ct = default)
    {
        var env = await ReadEnvelopeAsync(key, ct).ConfigureAwait(false);
        if (env is null) return;
        var stale = env with { IsStale = true };
        await _store.SetAsync(key, MemoryPackSerializer.Serialize(stale), ct: ct).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<string>> GetKeysWithPrefixAsync(string prefix, CancellationToken ct = default)
        => _store.GetKeysWithPrefixAsync(prefix, ct);

    /// <summary>Reads and validates the envelope, dropping entries whose schema stamp no longer matches.</summary>
    private async ValueTask<CacheEnvelope?> ReadEnvelopeAsync(string key, CancellationToken ct)
    {
        var raw = await _store.GetAsync(key, ct).ConfigureAwait(false);
        if (raw is null) return null;

        var env = MemoryPackSerializer.Deserialize<CacheEnvelope>(raw);
        if (env is null) return null;

        if (env.Schema != _schema)
        {
            // Incompatible (e.g. written by a previous app version) — drop rather than mis-deserialize.
            await _store.DeleteAsync(key, ct).ConfigureAwait(false);
            return null;
        }

        return env;
    }
}
