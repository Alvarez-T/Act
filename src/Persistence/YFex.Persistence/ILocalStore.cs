namespace YFex.Persistence;

/// <summary>
/// Synchronous, key-addressed byte store for small local documents that must be
/// read during construction (e.g. a consent record loaded before any async context
/// is available). Each key maps to a single durable blob.
/// </summary>
/// <remarks>
/// This is the synchronous counterpart to <see cref="ISnapshotStore"/>. Use it for
/// tiny configuration/consent state where an async, fire-and-forget snapshot API
/// would force otherwise-synchronous call sites to go async.
/// Implementations: <c>FileSystemLocalStore</c> (YFex.Persistence.FileSystem).
/// </remarks>
public interface ILocalStore
{
    /// <summary>Returns the bytes stored under <paramref name="key"/>, or <see langword="null"/> if absent.</summary>
    byte[]? Read(string key);

    /// <summary>Writes <paramref name="data"/> under <paramref name="key"/>, replacing any existing value.</summary>
    void Write(string key, byte[] data);

    /// <summary>Removes the entry for <paramref name="key"/>. No-op when not found.</summary>
    void Delete(string key);

    /// <summary>Returns <see langword="true"/> when a value exists for <paramref name="key"/>.</summary>
    bool Exists(string key);
}
