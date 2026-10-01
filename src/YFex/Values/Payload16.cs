using System.Runtime.CompilerServices;

namespace YFex;

/// <summary>
/// 16 bytes of inline storage for <see cref="RawValue"/>. Large enough for <see cref="decimal"/>
/// and <see cref="System.Guid"/>; smaller payloads (long, double, DateTime…) use the first 8 bytes.
/// Written and read with <see cref="Unsafe.As{TFrom,TTo}(ref TFrom)"/> — never boxed.
/// </summary>
[InlineArray(2)]
public struct Payload16
{
    private ulong _e0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Payload16 Of<T>(T value) where T : unmanaged
    {
        Payload16 p = default;
        Unsafe.As<Payload16, T>(ref p) = value;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly T Read<T>() where T : unmanaged
        => Unsafe.As<Payload16, T>(ref Unsafe.AsRef(in this));
}
