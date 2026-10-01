using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YFex.Helpers;

/// <summary>
/// Deterministic 64-bit hash (XXH64 algorithm, own implementation — no <c>System.IO.Hashing</c>
/// dependency). Same input → same value on every platform, process and version; safe to persist.
/// Use for fingerprints, cache keys and situation hashes. <b>Not</b> cryptographic.
/// <para>
/// Streaming: create, <c>Append</c> canonical little-endian primitives, then <see cref="Finish"/>.
/// Typed <c>Append</c> overloads write fixed-size encodings (strings are length-prefixed) so
/// concatenations cannot collide ("ab"+"c" ≠ "a"+"bc").
/// </para>
/// <code>
/// var h = StableHash64.Create();
/// h.Append(ruleCode); h.Append(amount);
/// ulong fingerprint = h.Finish();
/// </code>
/// Mutable struct: keep it in a local and pass by <c>ref</c>.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public struct StableHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    [InlineArray(32)]
    private struct Stripe { private byte _e0; }

    private ulong _v1, _v2, _v3, _v4;
    private readonly ulong _seed;
    private ulong _length;
    private Stripe _pending;
    private int _pendingCount;

    private StableHash64(ulong seed)
    {
        _seed = seed;
        _v1 = seed + P1 + P2;
        _v2 = seed + P2;
        _v3 = seed;
        _v4 = seed - P1;
        _length = 0;
        _pending = default;
        _pendingCount = 0;
    }

    /// <summary>Starts a new hash.</summary>
    public static StableHash64 Create(ulong seed = 0) => new(seed);

    /// <summary>One-shot hash of <paramref name="data"/>.</summary>
    public static ulong Hash(ReadOnlySpan<byte> data, ulong seed = 0)
    {
        var h = new StableHash64(seed);
        h.Append(data);
        return h.Finish();
    }

    /// <summary>One-shot hash of a string (UTF-16 little-endian code units, no length prefix).</summary>
    public static ulong Hash(ReadOnlySpan<char> text, ulong seed = 0)
    {
        var h = new StableHash64(seed);
        h.AppendChars(text);
        return h.Finish();
    }

    // ── Append ───────────────────────────────────────────────────────────────

    /// <summary>Appends raw bytes.</summary>
    public void Append(scoped ReadOnlySpan<byte> data)
    {
        _length += (ulong)data.Length;

        if (_pendingCount > 0)
        {
            int take = Math.Min(32 - _pendingCount, data.Length);
            data[..take].CopyTo(((Span<byte>)_pending)[_pendingCount..]);
            _pendingCount += take;
            data = data[take..];
            if (_pendingCount < 32) return;
            ConsumeStripe(_pending);
            _pendingCount = 0;
        }

        while (data.Length >= 32)
        {
            ConsumeStripe(data);
            data = data[32..];
        }

        if (!data.IsEmpty)
        {
            data.CopyTo(_pending);
            _pendingCount = data.Length;
        }
    }

    public void Append(byte value) => Append(new ReadOnlySpan<byte>(in value));

    public void Append(bool value) => Append(value ? (byte)1 : (byte)0);

    public void Append(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        Append(b);
    }

    public void Append(long value)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, value);
        Append(b);
    }

    public void Append(ulong value) => Append(unchecked((long)value));

    /// <summary>
    /// Appends a decimal normalized to its shortest scale, so numerically equal values
    /// (1.0m, 1.00m) produce the same hash.
    /// </summary>
    public void Append(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(Normalize(value), bits);
        for (int i = 0; i < 4; i++) Append(bits[i]);
    }

    /// <summary>Appends a length-prefixed string (<see langword="null"/> and "" differ).</summary>
    public void Append(string? value)
    {
        if (value is null) { Append(-1); return; }
        Append(value.Length);
        AppendChars(value);
    }

    /// <summary>Appends UTF-16 code units, little-endian, without a length prefix.</summary>
    public void AppendChars(scoped ReadOnlySpan<char> text)
    {
        if (BitConverter.IsLittleEndian)
        {
            Append(MemoryMarshal.AsBytes(text));
            return;
        }
        Span<byte> b = stackalloc byte[2];
        foreach (char c in text)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b, c);
            Append(b);
        }
    }

    // ── Finish ───────────────────────────────────────────────────────────────

    /// <summary>Returns the hash of everything appended so far (the state is not modified).</summary>
    public readonly ulong Finish()
    {
        ulong h;
        if (_length >= 32)
        {
            h = BitOperations.RotateLeft(_v1, 1) + BitOperations.RotateLeft(_v2, 7)
              + BitOperations.RotateLeft(_v3, 12) + BitOperations.RotateLeft(_v4, 18);
            h = MergeRound(h, _v1);
            h = MergeRound(h, _v2);
            h = MergeRound(h, _v3);
            h = MergeRound(h, _v4);
        }
        else
        {
            h = _seed + P5;
        }

        h += _length;

        ReadOnlySpan<byte> rest = ((ReadOnlySpan<byte>)_pending)[.._pendingCount];
        while (rest.Length >= 8)
        {
            h ^= Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rest));
            h = BitOperations.RotateLeft(h, 27) * P1 + P4;
            rest = rest[8..];
        }
        if (rest.Length >= 4)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1;
            h = BitOperations.RotateLeft(h, 23) * P2 + P3;
            rest = rest[4..];
        }
        foreach (byte b in rest)
        {
            h ^= b * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void ConsumeStripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong acc, ulong input)
    {
        acc += input * P2;
        acc = BitOperations.RotateLeft(acc, 31);
        return acc * P1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong MergeRound(ulong acc, ulong val)
    {
        acc ^= Round(0, val);
        return acc * P1 + P4;
    }

    // Dividing by 1 with 28 trailing zeros strips trailing zeros from the scale (1.500 → 1.5).
    private static decimal Normalize(decimal value) => value / 1.0000000000000000000000000000m;
}
