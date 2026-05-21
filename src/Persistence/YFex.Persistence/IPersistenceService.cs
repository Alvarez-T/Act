namespace YFex.Persistence;

/// <summary>DI-injectable facade for <see cref="PersistenceService"/>.</summary>
public interface IPersistenceService
{
    void Register(ISnapshotProvider provider);
    void Unregister(ISnapshotProvider provider);
    Task SaveSnapshotAsync(CancellationToken ct = default);
    Task RestoreSnapshotAsync(CancellationToken ct = default);
    Task ClearSnapshotAsync(string discriminator, CancellationToken ct = default);
}
