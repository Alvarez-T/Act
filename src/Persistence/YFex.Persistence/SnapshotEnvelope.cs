using MemoryPack;

namespace YFex.Persistence;

/// <summary>
/// Versioned wrapper stored in the <see cref="ISnapshotStore"/> for each
/// <see cref="ISnapshotProvider"/>. MemoryPack-serializable so it can be
/// stored as raw bytes without additional framing.
/// </summary>
[MemoryPackable]
public partial record SnapshotEnvelope
{
    /// <summary>Matches <see cref="ISnapshotProvider.Discriminator"/>.</summary>
    public string Discriminator { get; init; } = "";

    /// <summary>Schema version at capture time. Compared against <see cref="ISnapshotProvider.Version"/> on restore.</summary>
    public int Version { get; init; }

    /// <summary>Payload produced by <see cref="ISnapshotProvider.CaptureAsync"/>.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>UTC ticks when the snapshot was taken. Used for conflict resolution and diagnostics.</summary>
    public long CapturedAtUtcTicks { get; init; }
}
