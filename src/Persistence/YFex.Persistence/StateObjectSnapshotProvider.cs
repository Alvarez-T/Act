namespace YFex.Persistence;

/// <summary>
/// Generic <see cref="ISnapshotProvider"/> wrapper for any <see cref="IPersistableStateObject"/>.
/// The generator emits <c>CaptureSnapshot</c> / <c>RestoreSnapshot</c> on classes that carry
/// at least one <c>[Observable, Persist]</c> property; this class binds a live instance to
/// the <see cref="PersistenceService"/> without requiring manual serialization code.
/// </summary>
/// <typeparam name="T">A class implementing <see cref="IPersistableStateObject"/>, typically a
/// <c>StateObject</c> subclass with <c>[Persist]</c>-marked properties.</typeparam>
public sealed class StateObjectSnapshotProvider<T> : ISnapshotProvider
    where T : IPersistableStateObject
{
    private readonly T _stateObject;

    public string Discriminator { get; }
    public int Version          { get; }

    /// <param name="stateObject">The live instance whose <c>[Persist]</c> properties are snapshotted.</param>
    /// <param name="discriminator">Stable store key — typically the class name or a hand-chosen constant.</param>
    /// <param name="version">Schema version. Increment when the set of <c>[Persist]</c> properties changes.</param>
    public StateObjectSnapshotProvider(T stateObject, string discriminator, int version = 1)
    {
        _stateObject  = stateObject;
        Discriminator = discriminator;
        Version       = version;
    }

    /// <summary>
    /// Calls <see cref="IPersistableStateObject.CaptureSnapshot"/> on the wrapped instance.
    /// Returns <see langword="null"/> when the snapshot byte array is empty (nothing to persist).
    /// </summary>
    public ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default)
    {
        byte[] data = _stateObject.CaptureSnapshot();
        return ValueTask.FromResult<byte[]?>(data.Length == 0 ? null : data);
    }

    /// <summary>
    /// Calls <see cref="IPersistableStateObject.RestoreSnapshot"/> on the wrapped instance.
    /// Skips restoration when <paramref name="storedVersion"/> does not match <see cref="Version"/>.
    /// </summary>
    public ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default)
    {
        if (storedVersion != Version) return ValueTask.CompletedTask;
        _stateObject.RestoreSnapshot(data);
        return ValueTask.CompletedTask;
    }
}
