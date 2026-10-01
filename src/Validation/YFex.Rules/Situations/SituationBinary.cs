using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace YFex.Rules;

/// <summary>
/// Canonical binary writer for situations (audit/replay, 04 §1.3). Every scalar is a <see cref="RawValue"/> written
/// from its lossless parts (<see cref="RawValue.GetParts"/>):
/// <c>[kind][aux][hasText][type u32][low u64][high u64]([len u32][utf8])</c>, little-endian. Lines = <c>[count u32]</c> +
/// each line. The format is stable — bump <see cref="FormatVersion"/> on change (version 1 = no type id, legacy kinds).
/// </summary>
public ref struct SituationWriter(IBufferWriter<byte> buffer)
{
    /// <summary>Header byte written by <see cref="Situation.WriteTo{TS}"/>.</summary>
    public const byte FormatVersion = 2;

    private readonly IBufferWriter<byte> _buffer = buffer;

    public void WriteHeader() => WriteByte(FormatVersion);

    public void Write(in RawValue value)
    {
        value.GetParts(out var kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);
        var span = _buffer.GetSpan(23);
        span[0] = (byte)kind;
        span[1] = aux;
        span[2] = text is null ? (byte)0 : (byte)1;
        BinaryPrimitives.WriteUInt32LittleEndian(span[3..], type);
        BinaryPrimitives.WriteUInt64LittleEndian(span[7..], low);
        BinaryPrimitives.WriteUInt64LittleEndian(span[15..], high);
        _buffer.Advance(23);
        if (text is null) return;
        int max = Encoding.UTF8.GetMaxByteCount(text.Length);
        var t = _buffer.GetSpan(4 + max);
        int n = Encoding.UTF8.GetBytes(text, t[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(t, (uint)n);
        _buffer.Advance(4 + n);
    }

    /// <summary>Count prefix of a lines collection.</summary>
    public void WriteCount(int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.GetSpan(4), (uint)count);
        _buffer.Advance(4);
    }

    private void WriteByte(byte b)
    {
        _buffer.GetSpan(1)[0] = b;
        _buffer.Advance(1);
    }
}

/// <summary>Reader of <see cref="SituationWriter"/> output.</summary>
public ref struct SituationReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> _data = data;
    private byte _version = SituationWriter.FormatVersion;

    public readonly bool IsEnd => _data.IsEmpty;

    public void ReadHeader()
    {
        if (_data.IsEmpty || _data[0] is 0 or > SituationWriter.FormatVersion)
            throw new FormatException($"Unknown situation format (expected version ≤ {SituationWriter.FormatVersion}).");
        _version = _data[0];
        _data = _data[1..];
    }

    public RawValue Read()
    {
        bool v1 = _version == 1;
        int head = v1 ? 19 : 23;
        if (_data.Length < head) throw new FormatException("Truncated situation data.");
        byte kind = _data[0];
        byte aux = _data[1];
        bool hasText = _data[2] != 0;
        uint type = v1 ? 0 : BinaryPrimitives.ReadUInt32LittleEndian(_data[3..]);
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(_data[(head - 16)..]);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(_data[(head - 8)..]);
        _data = _data[head..];
        string? text = null;
        if (hasText)
        {
            int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data);
            text = Encoding.UTF8.GetString(_data.Slice(4, n));
            _data = _data[(4 + n)..];
        }
        return v1 ? RawValueLegacy.FromV1Parts(kind, low, high, text, aux) : RawValue.FromParts((ValueKind)kind, low, high, text, aux, type);
    }

    public int ReadCount()
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(_data);
        _data = _data[4..];
        return n;
    }

    /// <summary>Typed read for generated code (<see cref="RawValueConvert"/>); throws on a kind mismatch.</summary>
    public T Read<T>()
    {
        var raw = Read();
        if (raw.IsNone) return default!;
        return RawValueConvert.TryGet(in raw, out T value)
            ? value
            : throw new FormatException($"Situation data holds {raw.Kind}, expected {typeof(T).Name}.");
    }

    /// <summary>Typed enum read for generated code.</summary>
    public TEnum ReadEnum<TEnum>() where TEnum : unmanaged, Enum
    {
        var raw = Read();
        return raw.TryGetEnum(out TEnum value) ? value : default;
    }
}
