namespace YFex.Persistence;

/// <summary>
/// Implemented by source-generated code for any <c>StateObject</c> subclass that has
/// at least one <c>[Observable, Persist]</c> property.
/// The generator emits <c>CaptureSnapshot()</c> and <c>RestoreSnapshot()</c> using
/// MemoryPack to serialize only the marked properties.
/// </summary>
public interface IPersistableStateObject
{
    /// <summary>Serializes all <c>[Persist]</c> property values to a MemoryPack byte array.</summary>
    byte[] CaptureSnapshot();

    /// <summary>
    /// Restores <c>[Persist]</c> property values from bytes previously produced by
    /// <see cref="CaptureSnapshot"/>. Version mismatches should be handled by the
    /// wrapping <see cref="ISnapshotProvider"/>.
    /// </summary>
    void RestoreSnapshot(ReadOnlySpan<byte> data);
}
