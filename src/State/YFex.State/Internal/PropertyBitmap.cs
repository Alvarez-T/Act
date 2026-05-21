using System.Numerics;
using System.Runtime.CompilerServices;

namespace YFex.State.Internal;

/// <summary>
/// Compact pending-change bitmap. The generator picks the right primitive:
/// ≤32 properties → uint32; 33–64 → uint64; >64 → InlineArray of ulong.
/// This file provides the small-property helpers used by the base class directly.
/// </summary>
public struct PropertyBitmap32
{
    private uint _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(uint id) => _bits |= 1u << (int)id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Or(in PropertyBitmap32 other) => _bits |= other._bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty() => _bits == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _bits = 0;

    /// <summary>
    /// Drains set bits lowest-first via TrailingZeroCount — O(set bits), not O(width).
    /// </summary>
    public uint PopLowest()
    {
        uint bit = (uint)BitOperations.TrailingZeroCount(_bits);
        _bits &= _bits - 1; // clear lowest set bit
        return bit;
    }
}

public struct PropertyBitmap64
{
    private ulong _bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(uint id) => _bits |= 1UL << (int)id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Or(in PropertyBitmap64 other) => _bits |= other._bits;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEmpty() => _bits == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _bits = 0;

    public uint PopLowest()
    {
        uint bit = (uint)BitOperations.TrailingZeroCount(_bits);
        _bits &= _bits - 1;
        return bit;
    }
}
