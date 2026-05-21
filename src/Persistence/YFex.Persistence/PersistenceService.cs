using MemoryPack;

namespace YFex.Persistence;

/// <summary>
/// Orchestrates save and restore across all registered <see cref="ISnapshotProvider"/> instances.
/// Access the singleton via <see cref="Current"/> or inject <see cref="IPersistenceService"/>
/// from DI. Configure once at startup with <see cref="Configure"/>.
/// </summary>
public sealed class PersistenceService : IPersistenceService
{
    private static PersistenceService? _instance;

    /// <summary>The process-wide singleton. Throws when not yet configured.</summary>
    public static PersistenceService Current => _instance
        ?? throw new InvalidOperationException(
            "PersistenceService is not configured. Call AddYFexPersistence() in your DI setup.");

    /// <summary>Replaces the process-wide singleton. Called by DI registration.</summary>
    public static void Configure(PersistenceService service) => _instance = service;

    private readonly ISnapshotStore _store;
    private readonly List<ISnapshotProvider> _providers = new();
    private readonly object _lock = new();

    public PersistenceService(ISnapshotStore store)
    {
        _store = store;
    }

    /// <summary>Adds a provider to the snapshot set. Safe to call at any time.</summary>
    public void Register(ISnapshotProvider provider)
    {
        lock (_lock) _providers.Add(provider);
    }

    /// <summary>Removes a previously registered provider.</summary>
    public void Unregister(ISnapshotProvider provider)
    {
        lock (_lock) _providers.Remove(provider);
    }

    /// <summary>
    /// Captures all registered providers and writes them to the store.
    /// Providers that return <see langword="null"/> from <see cref="ISnapshotProvider.CaptureAsync"/>
    /// are skipped (no write, existing snapshot preserved).
    /// </summary>
    public async Task SaveSnapshotAsync(CancellationToken ct = default)
    {
        ISnapshotProvider[] snapshot;
        lock (_lock) snapshot = _providers.ToArray();

        foreach (var provider in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? data = await provider.CaptureAsync(ct).ConfigureAwait(false);
            if (data is null) continue;

            var envelope = new SnapshotEnvelope
            {
                Discriminator    = provider.Discriminator,
                Version          = provider.Version,
                Data             = data,
                CapturedAtUtcTicks = DateTime.UtcNow.Ticks,
            };
            byte[] bytes = MemoryPackSerializer.Serialize(envelope);
            await _store.SaveAsync(StoreKey(provider.Discriminator), bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads and restores all registered providers from the store.
    /// Missing or unreadable snapshots are skipped; version mismatches are delegated to
    /// the provider's <see cref="ISnapshotProvider.RestoreAsync"/>.
    /// </summary>
    public async Task RestoreSnapshotAsync(CancellationToken ct = default)
    {
        ISnapshotProvider[] snapshot;
        lock (_lock) snapshot = _providers.ToArray();

        foreach (var provider in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            byte[]? raw = await _store.LoadAsync(StoreKey(provider.Discriminator), ct).ConfigureAwait(false);
            if (raw is null) continue;

            SnapshotEnvelope? envelope;
            try { envelope = MemoryPackSerializer.Deserialize<SnapshotEnvelope>(raw); }
            catch { continue; }
            if (envelope is null) continue;

            try
            {
                await provider.RestoreAsync(envelope.Data, envelope.Version, ct).ConfigureAwait(false);
            }
            catch { /* skip broken snapshots — log in a real app */ }
        }
    }

    /// <summary>Clears the snapshot for one provider from the store.</summary>
    public Task ClearSnapshotAsync(string discriminator, CancellationToken ct = default)
        => _store.DeleteAsync(StoreKey(discriminator), ct);

    private static string StoreKey(string discriminator) => $"yfex:snapshot:{discriminator}";
}
