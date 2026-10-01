using System.Diagnostics.CodeAnalysis;
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

/// <summary>
/// MemoryPack-backed <see cref="IPersistenceSerializer"/> (AOT-friendly, zero-alloc).
/// <para>Under trimming/NativeAOT, <c>T</c> must be <c>[MemoryPackable]</c> or a type MemoryPack
/// ships a built-in formatter for (primitives, strings, arrays/collections of those).</para>
/// </summary>
public sealed class MemoryPackPersistenceSerializer : IPersistenceSerializer
{
    public static readonly MemoryPackPersistenceSerializer Instance = new();

    public byte[] Serialize<T>(T value) => MemoryPackSerializer.Serialize(value);

    // MemoryPack's DAM(All) on T only protects its reflection fallback for unregistered types. The
    // supported contract here is source-generated [MemoryPackable] types (formatter registered by the
    // generated static ctor) or built-in formatters, neither of which reflects. Propagating DAM(All)
    // instead would ripple through ICache and every cache implementation for no runtime benefit.
    [UnconditionalSuppressMessage("Trimming", "IL2091",
        Justification = "T is required to be [MemoryPackable] or have a built-in MemoryPack formatter; see class remarks.")]
    public T? Deserialize<T>(byte[] data) => MemoryPackSerializer.Deserialize<T>(data);
}
