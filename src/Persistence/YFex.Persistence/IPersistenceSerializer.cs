using MemoryPack;

namespace YFex.Persistence;

/// <summary>
/// Pluggable value serializer for the persistence stack (cache values, queue items).
/// Default implementation is <see cref="MemoryPackPersistenceSerializer"/>.
/// </summary>
public interface IPersistenceSerializer
{
    byte[] Serialize<T>(T value);
    T? Deserialize<T>(byte[] data);
}

/// <summary>MemoryPack-backed <see cref="IPersistenceSerializer"/> (AOT-friendly, zero-alloc).</summary>
public sealed class MemoryPackPersistenceSerializer : IPersistenceSerializer
{
    public static readonly MemoryPackPersistenceSerializer Instance = new();

    public byte[] Serialize<T>(T value) => MemoryPackSerializer.Serialize(value);

    public T? Deserialize<T>(byte[] data) => MemoryPackSerializer.Deserialize<T>(data);
}
