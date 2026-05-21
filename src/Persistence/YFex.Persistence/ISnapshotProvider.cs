namespace YFex.Persistence;

/// <summary>
/// Captures and restores the state of one named subsystem.
/// Register with <see cref="PersistenceService.Register"/> at startup.
/// </summary>
public interface ISnapshotProvider
{
    /// <summary>
    /// Unique key used to store this provider's snapshot in the <see cref="ISnapshotStore"/>.
    /// Must be stable across app versions (e.g. the class name or a hand-chosen constant).
    /// </summary>
    string Discriminator { get; }

    /// <summary>
    /// Schema version. When a stored snapshot has a different version,
    /// <see cref="RestoreAsync"/> receives <paramref name="storedVersion"/> so
    /// the provider can migrate or skip gracefully.
    /// </summary>
    int Version { get; }

    /// <summary>
    /// Serializes the current state to bytes. Returns <see langword="null"/>
    /// when there is nothing to persist (e.g. the state is empty or default).
    /// </summary>
    ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default);

    /// <summary>
    /// Restores state from bytes previously produced by <see cref="CaptureAsync"/>.
    /// <paramref name="storedVersion"/> is the schema version recorded at capture time.
    /// Skip or migrate if <paramref name="storedVersion"/> != <see cref="Version"/>.
    /// </summary>
    ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default);
}
