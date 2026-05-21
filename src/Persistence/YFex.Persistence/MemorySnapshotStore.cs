using System.Collections.Concurrent;

namespace YFex.Persistence;

/// <summary>
/// In-memory <see cref="ISnapshotStore"/> for unit tests and design-time previews.
/// Not suitable for production — data is lost on process restart.
/// </summary>
public sealed class MemorySnapshotStore : ISnapshotStore
{
    private readonly ConcurrentDictionary<string, byte[]> _store = new();

    public Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
    {
        _store[key] = data;
        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default)
    {
        _store.TryGetValue(key, out byte[]? data);
        return Task.FromResult(data);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _store.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>Removes all snapshots. Useful between tests.</summary>
    public void Clear() => _store.Clear();
}
