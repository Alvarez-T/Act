namespace YFex.Persistence;

/// <summary>
/// Durable key-value store for snapshot bytes.
/// Implementations: <see cref="MemorySnapshotStore"/> (testing),
/// <c>FileSystemSnapshotStore</c> (desktop), <c>IndexedDbSnapshotStore</c> (Blazor WASM).
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Persists <paramref name="data"/> under <paramref name="key"/>.</summary>
    Task SaveAsync(string key, byte[] data, CancellationToken ct = default);

    /// <summary>Returns the data saved under <paramref name="key"/>, or <see langword="null"/> if not found.</summary>
    Task<byte[]?> LoadAsync(string key, CancellationToken ct = default);

    /// <summary>Removes the entry for <paramref name="key"/>. No-op when not found.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
}
