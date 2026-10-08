using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace YFex.Diagnostics;

/// <summary>
/// Decodes the entries written by <see cref="LogWriter"/> (consumer thread, sinks, tests). Never throws on malformed input:
/// <see cref="TryRead"/> returns false and <see cref="IsMalformed"/> becomes true.
/// </summary>
public ref struct LogFieldReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private int _depth;

    public LogFieldReader(ReadOnlySpan<byte> data) => _data = data;

    /// <summary>Nesting depth after the last entry read (ObjectStart increments, ObjectEnd decrements).</summary>
    public readonly int Depth => _depth;

    /// <summary>True when decoding stopped on an entry that did not fit the data or had an unknown kind.</summary>
    public bool IsMalformed { get; private set; }

    /// <summary>Reads the next entry; false at the end of the data (or on malformed data).</summary>
    public bool TryRead(out LogField field)
    {
        field = default;
        if (_position + 3 > _data.Length)
        {
            IsMalformed = _position != _data.Length;
            return false;
        }

        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        var kind = (LogValueKind)_data[_position + 2];
        ReadOnlySpan<byte> rest = _data[(_position + 3)..];
        int length = PayloadLength(kind, rest);
        if (length < 0 || length > rest.Length)
        {
            IsMalformed = true;
            return false;
        }

        field = new LogField(id, kind, rest[..length]);
        _position += 3 + length;
        if (kind == LogValueKind.ObjectStart) _depth++;
        else if (kind == LogValueKind.ObjectEnd && _depth > 0) _depth--;
        return true;
    }

    private static int PayloadLength(LogValueKind kind, ReadOnlySpan<byte> rest) => kind switch
    {
        LogValueKind.Null or LogValueKind.ObjectEnd => 0,
        LogValueKind.Bool or LogValueKind.Omitted => 1,
        LogValueKind.ObjectStart => 2,
        LogValueKind.Int32 or LogValueKind.DateOnly or LogValueKind.Count => 4,
        LogValueKind.Int64 or LogValueKind.UInt64 or LogValueKind.Double or LogValueKind.TimeSpan
            or LogValueKind.TimeOnly or LogValueKind.Length => 8,
        LogValueKind.DateTime => 9,
        LogValueKind.DateTimeOffset or LogValueKind.Enum => 10,
        LogValueKind.Decimal or LogValueKind.Guid => 16,
        LogValueKind.Text => TextLength(rest, 0, 2),
        LogValueKind.Utf8 => TextLength(rest, 0, 1),
        LogValueKind.Raw => RawLength(rest),
        _ => -1,
    };

    private static int TextLength(ReadOnlySpan<byte> rest, int offset, int unitSize)
        => rest.Length < offset + 3 ? -1 : 3 + BinaryPrimitives.ReadUInt16LittleEndian(rest[(offset + 1)..]) * unitSize;

    private static int RawLength(ReadOnlySpan<byte> rest)
    {
        if (rest.Length < 22) return -1;
        var kind = (ValueKind)rest[0];
        if (kind is not (ValueKind.Text or ValueKind.EnumCode)) return 22;
        int text = TextLength(rest, 22, 2);
        return text < 0 ? -1 : 22 + text;
    }
}

/// <summary>One decoded entry. Accessors assume the matching <see cref="Kind"/> (they return defaults otherwise).</summary>
public readonly ref struct LogField
{
    private readonly ReadOnlySpan<byte> _payload;

    public LogField(ushort field, LogValueKind kind, ReadOnlySpan<byte> payload)
    {
        Field = field;
        Kind = kind;
        _payload = payload;
    }

    /// <summary>Field id (descriptor/shape index, or a name reference when <see cref="IsNamed"/>).</summary>
    public ushort Field { get; }

    public LogValueKind Kind { get; }

    /// <summary>Raw payload bytes.</summary>
    public ReadOnlySpan<byte> Payload => _payload;

    /// <summary>True when <see cref="Field"/> references a name in the record's reference table.</summary>
    public bool IsNamed => Field != LogWriter.NoField && (Field & LogWriter.NamedFieldFlag) != 0;

    /// <summary>Index of the name in the record's reference table (only when <see cref="IsNamed"/>).</summary>
    public int NameReference => Field & ~LogWriter.NamedFieldFlag;

    public bool GetBool() => Kind == LogValueKind.Bool && _payload[0] != 0;

    public int GetInt32() => Kind is LogValueKind.Int32 or LogValueKind.Count or LogValueKind.DateOnly
        ? BinaryPrimitives.ReadInt32LittleEndian(_payload) : 0;

    /// <summary>Int64, TimeSpan/TimeOnly ticks, Length, Enum underlying value; Int32/Count widened.</summary>
    public long GetInt64() => Kind switch
    {
        LogValueKind.Int64 or LogValueKind.TimeSpan or LogValueKind.TimeOnly or LogValueKind.Length or LogValueKind.Enum
            or LogValueKind.DateTime or LogValueKind.DateTimeOffset => BinaryPrimitives.ReadInt64LittleEndian(_payload),
        LogValueKind.Int32 or LogValueKind.Count or LogValueKind.DateOnly => BinaryPrimitives.ReadInt32LittleEndian(_payload),
        LogValueKind.UInt64 => unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(_payload)),
        LogValueKind.Bool => _payload[0],
        _ => 0,
    };

    public ulong GetUInt64() => Kind == LogValueKind.UInt64 ? BinaryPrimitives.ReadUInt64LittleEndian(_payload) : unchecked((ulong)GetInt64());

    public double GetDouble() => Kind == LogValueKind.Double ? BinaryPrimitives.ReadDoubleLittleEndian(_payload) : GetInt64();

    public decimal GetDecimal()
    {
        if (Kind != LogValueKind.Decimal) return GetInt64();
        Span<int> bits = stackalloc int[4];
        for (int i = 0; i < 4; i++) bits[i] = BinaryPrimitives.ReadInt32LittleEndian(_payload[(i * 4)..]);
        return new decimal(bits);
    }

    public Guid GetGuid() => Kind == LogValueKind.Guid ? new Guid(_payload[..16]) : Guid.Empty;

    public DateTime GetDateTime() => Kind == LogValueKind.DateTime
        ? new DateTime(BinaryPrimitives.ReadInt64LittleEndian(_payload), (DateTimeKind)_payload[8]) : default;

    public DateTimeOffset GetDateTimeOffset() => Kind == LogValueKind.DateTimeOffset
        ? new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(_payload), TimeSpan.FromMinutes(BinaryPrimitives.ReadInt16LittleEndian(_payload[8..])))
        : default;

    public TimeSpan GetTimeSpan() => Kind == LogValueKind.TimeSpan ? new TimeSpan(GetInt64()) : default;

    public DateOnly GetDateOnly() => Kind == LogValueKind.DateOnly ? DateOnly.FromDayNumber(GetInt32()) : default;

    public TimeOnly GetTimeOnly() => Kind == LogValueKind.TimeOnly ? new TimeOnly(GetInt64()) : default;

    /// <summary>UTF-16 text of <see cref="LogValueKind.Text"/> entries and of text-bearing <see cref="LogValueKind.Raw"/> entries.</summary>
    public ReadOnlySpan<char> GetText(out LogTextFlags flags)
    {
        flags = LogTextFlags.None;
        ReadOnlySpan<byte> text;
        if (Kind == LogValueKind.Text) text = _payload;
        else if (Kind == LogValueKind.Raw && _payload.Length > 22) text = _payload[22..];
        else return default;
        flags = (LogTextFlags)text[0];
        int chars = BinaryPrimitives.ReadUInt16LittleEndian(text[1..]);
        return MemoryMarshal.Cast<byte, char>(text.Slice(3, chars * 2));
    }

    /// <summary>UTF-8 text of <see cref="LogValueKind.Utf8"/> entries.</summary>
    public ReadOnlySpan<byte> GetUtf8(out LogTextFlags flags)
    {
        flags = LogTextFlags.None;
        if (Kind != LogValueKind.Utf8) return default;
        flags = (LogTextFlags)_payload[0];
        return _payload.Slice(3, BinaryPrimitives.ReadUInt16LittleEndian(_payload[1..]));
    }

    /// <summary>The <see cref="RawValue"/> of a <see cref="LogValueKind.Raw"/> entry (allocates a string for text kinds).</summary>
    public RawValue GetRaw()
    {
        if (Kind != LogValueKind.Raw) return RawValue.None;
        var kind = (ValueKind)_payload[0];
        string? text = kind is ValueKind.Text or ValueKind.EnumCode ? new string(GetText(out _)) : null;
        return RawValue.FromParts(kind, BinaryPrimitives.ReadUInt64LittleEndian(_payload[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(_payload[14..]), text, _payload[1], BinaryPrimitives.ReadUInt32LittleEndian(_payload[2..]));
    }

    /// <summary>Reference-table index of the enum's <see cref="LogEnumNames"/> (-1 = none).</summary>
    public int GetEnumNamesReference() => Kind == LogValueKind.Enum ? BinaryPrimitives.ReadInt16LittleEndian(_payload[8..]) : -1;

    /// <summary>Reference-table index of the object's name table (-1 = none).</summary>
    public int GetObjectNamesReference() => Kind == LogValueKind.ObjectStart ? BinaryPrimitives.ReadInt16LittleEndian(_payload) : -1;

    public LogOmission GetOmission() => Kind == LogValueKind.Omitted ? (LogOmission)_payload[0] : default;
}
