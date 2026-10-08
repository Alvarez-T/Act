using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YFex.Privacy;

namespace YFex.Diagnostics;

/// <summary>
/// Encoding of one field value inside a record. Every entry is <c>[field u16][kind u8][payload]</c>, little-endian.
/// <see cref="LogFieldReader"/> is the matching decoder. Text is copied as UTF-16 by producers and transcoded on the consumer.
/// </summary>
public enum LogValueKind : byte
{
    /// <summary>No payload: the member was null.</summary>
    Null = 0,
    Bool = 1,       // u8
    Int32 = 2,      // i32
    Int64 = 3,      // i64
    UInt64 = 4,     // u64
    Double = 5,     // f64
    Decimal = 6,    // 4 × i32 (decimal.GetBits order)
    Guid = 7,       // 16 bytes
    DateTime = 8,   // i64 ticks + u8 DateTimeKind
    DateTimeOffset = 9, // i64 ticks + i16 offset minutes
    TimeSpan = 10,  // i64 ticks
    DateOnly = 11,  // i32 day number
    TimeOnly = 12,  // i64 ticks

    /// <summary><c>[flags u8][chars u16][UTF-16]</c>, see <see cref="LogTextFlags"/>.</summary>
    Text = 13,

    /// <summary><c>[flags u8][bytes u16][UTF-8]</c>: output of <see cref="IUtf8SpanFormattable"/>.</summary>
    Utf8 = 14,

    /// <summary>A <see cref="RawValue"/>: <c>[kind u8][aux u8][type u32][low u64][high u64]</c> + a Text payload for text kinds.</summary>
    Raw = 15,

    /// <summary><c>[underlying i64][names reference i16]</c>; the reference points to a <see cref="LogEnumNames"/> (-1 = none).</summary>
    Enum = 16,

    /// <summary>Collection size (<c>i32</c>): collections are captured as counts by default.</summary>
    Count = 17,

    /// <summary>Byte length (<c>i64</c>) of a blob or stream: never its content.</summary>
    Length = 18,

    /// <summary>A value existed but was not recorded: <c>[reason u8]</c>, see <see cref="LogOmission"/>.</summary>
    Omitted = 19,

    /// <summary>Start of a nested object: <c>[names reference i16]</c> (a <see cref="LogShapeFor{T}"/> name table, -1 = none).</summary>
    ObjectStart = 20,

    /// <summary>End of the innermost nested object (no payload).</summary>
    ObjectEnd = 21,
}

/// <summary>Flags of <see cref="LogValueKind.Text"/> and <see cref="LogValueKind.Utf8"/> payloads.</summary>
[Flags]
public enum LogTextFlags : byte
{
    None = 0,

    /// <summary>The source was longer than the cap; only a prefix was copied.</summary>
    Truncated = 1,

    /// <summary>Free text that the consumer must scrub (personal-data patterns) before it leaves the process.</summary>
    Scrub = 2,

    /// <summary>Already masked on the producer by the value's privacy classification (YFex.Privacy): shown as is, never scrubbed.</summary>
    Masked = 4,
}

/// <summary>Why a value was <see cref="LogValueKind.Omitted"/>.</summary>
public enum LogOmission : byte
{
    /// <summary>Never recorded by policy (secret-looking member, <c>[NoLog]</c>).</summary>
    Erased = 0,

    /// <summary>Nested deeper than <see cref="LogWriter.MaxDepth"/>.</summary>
    DepthLimit = 1,

    /// <summary>The type has no log shape and no allocation-free way to write itself.</summary>
    NoShape = 2,

    /// <summary>The value did not fit even after growing the record.</summary>
    TooLarge = 3,

    /// <summary>
    /// Classified sensitive, financial or secret by YFex.Privacy (<see cref="DataSensitivity.SensitivePersonal"/> and up), or a
    /// member of a record classified as a whole: never recorded.
    /// </summary>
    Sensitive = 4,
}

/// <summary>
/// Record storage behind a <see cref="LogWriter"/>, implemented by the engine's pooled slot. Lets the writer grow once into a
/// larger slot and keep object references (names, enum tables) out of the byte span.
/// </summary>
public abstract class LogRecordStorage
{
    /// <summary>
    /// Returns a larger span whose first <paramref name="written"/> bytes equal the current ones and with at least
    /// <paramref name="required"/> free bytes after them, or an empty span when the record cannot grow.
    /// </summary>
    public abstract Span<byte> Grow(int written, int required);

    /// <summary>Stores a reference in the record's reference table. Returns its index, or -1 when the table is full.</summary>
    public abstract int AddReference(object value);
}

/// <summary>
/// Writes field values into a record without boxing, allocating or throwing. Every overload is primitive or generic-constrained.
/// When the record is full it grows once through its <see cref="LogRecordStorage"/>; after that, further values are dropped and
/// <see cref="IsTruncated"/> becomes true (the engine then sets <see cref="OperationFlags.Truncated"/>).
/// <para>
/// Field ids below <see cref="NamedFieldFlag"/> index the descriptor's <see cref="OperationDescriptor.Fields"/> (or the enclosing
/// shape's name table). Ids with the flag set reference a name stored through <see cref="Field(string)"/>.
/// </para>
/// </summary>
public ref struct LogWriter
{
    /// <summary>Bit set on field ids that reference a name in the record's reference table.</summary>
    public const ushort NamedFieldFlag = 0x8000;

    /// <summary>Field id meaning "no field could be allocated": writes with it are dropped.</summary>
    public const ushort NoField = 0xFFFF;

    /// <summary>Deepest nesting of objects (log shapes inside log shapes).</summary>
    public const int MaxDepth = 2;

    private const int HeaderSize = 3;

    private Span<byte> _buffer;
    private readonly LogRecordStorage? _storage;
    private int _position;
    private int _fieldCount;
    private int _depth;
    private bool _truncated;
    private bool _grown;

    /// <summary>Maximum UTF-16 characters copied per text value (default 512, i.e. 1 KiB). Set by the engine at install.</summary>
    public static int MaxTextCharacters { get; set; } = 512;

    /// <summary>Maximum field entries per record (default 32). Set by the engine at install.</summary>
    public static int MaxFieldsPerRecord { get; set; } = 32;

    public LogWriter(Span<byte> buffer, LogRecordStorage? storage = null)
    {
        _buffer = buffer;
        _storage = storage;
    }

    /// <summary>Bytes written so far.</summary>
    public readonly int Position => _position;

    /// <summary>Entries written (values and object markers).</summary>
    public readonly int FieldCount => _fieldCount;

    /// <summary>True once a value was dropped for lack of space or fields.</summary>
    public readonly bool IsTruncated => _truncated;

    /// <summary>The encoded entries.</summary>
    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    // ── Names ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A field id for an ad-hoc name (interpolated-string holes, MEL bridge). The string reference is stored, not copied, so pass
    /// literals or other long-lived strings. Returns <see cref="NoField"/> when there is no reference table or it is full.
    /// </summary>
    public ushort Field(string name)
    {
        if (_storage is null) return NoField;
        int index = _storage.AddReference(name);
        return index is >= 0 and < NamedFieldFlag ? (ushort)(NamedFieldFlag | index) : NoField;
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    public void WriteNull(ushort field) => TryReserve(field, LogValueKind.Null, 0, out _);

    public void Write(ushort field, bool value)
    {
        if (TryReserve(field, LogValueKind.Bool, 1, out Span<byte> d)) d[0] = value ? (byte)1 : (byte)0;
    }

    public void Write(ushort field, int value)
    {
        if (TryReserve(field, LogValueKind.Int32, 4, out Span<byte> d)) BinaryPrimitives.WriteInt32LittleEndian(d, value);
    }

    public void Write(ushort field, long value)
    {
        if (TryReserve(field, LogValueKind.Int64, 8, out Span<byte> d)) BinaryPrimitives.WriteInt64LittleEndian(d, value);
    }

    public void Write(ushort field, ulong value)
    {
        if (TryReserve(field, LogValueKind.UInt64, 8, out Span<byte> d)) BinaryPrimitives.WriteUInt64LittleEndian(d, value);
    }

    public void Write(ushort field, double value)
    {
        if (TryReserve(field, LogValueKind.Double, 8, out Span<byte> d)) BinaryPrimitives.WriteDoubleLittleEndian(d, value);
    }

    public void Write(ushort field, decimal value)
    {
        if (TryReserve(field, LogValueKind.Decimal, 16, out Span<byte> d)) WriteDecimal(d, value);
    }

    public void Write(ushort field, Guid value)
    {
        if (TryReserve(field, LogValueKind.Guid, 16, out Span<byte> d)) value.TryWriteBytes(d);
    }

    public void Write(ushort field, DateTime value)
    {
        if (!TryReserve(field, LogValueKind.DateTime, 9, out Span<byte> d)) return;
        BinaryPrimitives.WriteInt64LittleEndian(d, value.Ticks);
        d[8] = (byte)value.Kind;
    }

    public void Write(ushort field, DateTimeOffset value)
    {
        if (!TryReserve(field, LogValueKind.DateTimeOffset, 10, out Span<byte> d)) return;
        BinaryPrimitives.WriteInt64LittleEndian(d, value.Ticks);
        BinaryPrimitives.WriteInt16LittleEndian(d[8..], (short)value.TotalOffsetMinutes);
    }

    public void Write(ushort field, TimeSpan value)
    {
        if (TryReserve(field, LogValueKind.TimeSpan, 8, out Span<byte> d)) BinaryPrimitives.WriteInt64LittleEndian(d, value.Ticks);
    }

    public void Write(ushort field, DateOnly value)
    {
        if (TryReserve(field, LogValueKind.DateOnly, 4, out Span<byte> d)) BinaryPrimitives.WriteInt32LittleEndian(d, value.DayNumber);
    }

    public void Write(ushort field, TimeOnly value)
    {
        if (TryReserve(field, LogValueKind.TimeOnly, 8, out Span<byte> d)) BinaryPrimitives.WriteInt64LittleEndian(d, value.Ticks);
    }

    // ── Text ─────────────────────────────────────────────────────────────────

    /// <summary>UTF-16 copy, capped at <see cref="MaxTextCharacters"/>. Transcoded on the consumer.</summary>
    public void Write(ushort field, scoped ReadOnlySpan<char> value) => WriteText(field, value, MaxTextCharacters, LogTextFlags.None);

    /// <summary>Null writes <see cref="LogValueKind.Null"/>.</summary>
    public void Write(ushort field, string? value)
    {
        if (value is null) WriteNull(field);
        else WriteText(field, value, MaxTextCharacters, LogTextFlags.None);
    }

    /// <summary>Free text the consumer scrubs before it leaves the process (raw strings of messages, 04 §3). Null writes Null.</summary>
    public void WriteScrubbed(ushort field, string? value, int maxCharacters = 256)
    {
        if (value is null) WriteNull(field);
        else WriteText(field, value, Math.Min(maxCharacters, MaxTextCharacters), LogTextFlags.Scrub);
    }

    /// <inheritdoc cref="WriteScrubbed(ushort, string?, int)"/>
    public void WriteScrubbed(ushort field, scoped ReadOnlySpan<char> value, int maxCharacters = 256)
        => WriteText(field, value, Math.Min(maxCharacters, MaxTextCharacters), LogTextFlags.Scrub);

    // ── Privacy (YFex.Privacy classification, privacy design 08 §7) ──────────

    /// <summary>
    /// A value of a <see cref="SensitiveAttribute"/> type. Personal data is written as its display mask (generated, no allocation);
    /// sensitive, financial and secret data is never recorded (<see cref="LogOmission.Sensitive"/> / <see cref="LogOmission.Erased"/>).
    /// <paramref name="floor"/> raises the level for members of a record classified as a whole (02 §5.5).
    /// </summary>
    public void WriteClassified<T>(ushort field, in T value, DataSensitivity floor = DataSensitivity.Public) where T : IClassifiedData<T>
    {
        DataSensitivity level = T.Classify(in value).Sensitivity.Max(floor);
        if (level.IsNeverRecorded())
        {
            WriteOmitted(field, level == DataSensitivity.Secret ? LogOmission.Erased : LogOmission.Sensitive);
            return;
        }
        int capacity = Math.Clamp(T.MaxMaskedLength, Masks.Hidden.Length, MaxTextCharacters);
        Span<char> masked = stackalloc char[capacity];
        if (!T.TryWriteMasked(in value, masked, out int written))
        {
            WriteOmitted(field, LogOmission.TooLarge);
            return;
        }
        WriteText(field, masked[..written], MaxTextCharacters, level.IsPersonal() ? LogTextFlags.Masked : LogTextFlags.None);
    }

    /// <summary>
    /// Raw text that is personal data (a member declared <c>[Sensitive]</c>, or a text member of a record classified as a whole):
    /// written with the level's default mask (<see cref="Masks.DefaultPreset"/>), or not at all from SensitivePersonal up.
    /// Null writes Null.
    /// </summary>
    public void WriteMaskedText(ushort field, string? value, DataSensitivity sensitivity)
    {
        if (value is null) WriteNull(field);
        else WriteMaskedText(field, value.AsSpan(), sensitivity);
    }

    /// <inheritdoc cref="WriteMaskedText(ushort, string?, DataSensitivity)"/>
    public void WriteMaskedText(ushort field, scoped ReadOnlySpan<char> value, DataSensitivity sensitivity)
    {
        if (sensitivity.IsNeverRecorded())
        {
            WriteOmitted(field, sensitivity == DataSensitivity.Secret ? LogOmission.Erased : LogOmission.Sensitive);
            return;
        }
        MaskPreset preset = Masks.DefaultPreset(sensitivity, out int count);
        if (preset == MaskPreset.None)
        {
            WriteText(field, value, MaxTextCharacters, LogTextFlags.Scrub);
            return;
        }
        Span<char> masked = stackalloc char[Masks.MaxLength(preset, count, 0)];
        if (Masks.TryWritePreset(value, preset, count, masked, out int written)) WriteText(field, masked[..written], MaxTextCharacters, LogTextFlags.Masked);
        else WriteOmitted(field, LogOmission.Sensitive);
    }

    /// <summary>A non-text member of a record classified as a whole (02 §5.5): records only that a value existed.</summary>
    public void WriteSensitive(ushort field) => WriteOmitted(field, LogOmission.Sensitive);

    // ── Value objects ────────────────────────────────────────────────────────

    /// <summary>A core <see cref="RawValue"/> (Money, Percentual, Quantity… after <c>ToRaw()</c>).</summary>
    public void WriteRaw(ushort field, in RawValue value)
    {
        value.GetParts(out ValueKind kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);
        bool hasText = kind is ValueKind.Text or ValueKind.EnumCode;   // these kinds always carry a text section
        text ??= string.Empty;
        int textChars = hasText ? Math.Min(text.Length, MaxTextCharacters) : 0;
        int payload = 22 + (hasText ? 3 + textChars * 2 : 0);
        if (!TryReserve(field, LogValueKind.Raw, payload, out Span<byte> d)) return;
        d[0] = (byte)kind;
        d[1] = aux;
        BinaryPrimitives.WriteUInt32LittleEndian(d[2..], type);
        BinaryPrimitives.WriteUInt64LittleEndian(d[6..], low);
        BinaryPrimitives.WriteUInt64LittleEndian(d[14..], high);
        if (hasText) WriteTextPayload(d[22..], text.AsSpan(0, textChars), textChars < text.Length ? LogTextFlags.Truncated : LogTextFlags.None);
    }

    /// <summary>Any <see cref="IRawValue{TSelf}"/> type (struct: no boxing).</summary>
    public void WriteRawValue<T>(ushort field, in T value) where T : IRawValue<T>
    {
        RawValue raw = value.ToRaw();
        WriteRaw(field, in raw);
    }

    /// <summary>A value object that writes itself (<see cref="ILogValue{TSelf}"/>, usually generated from <c>[LogValue]</c>).</summary>
    public void Write<T>(ushort field, in T value) where T : ILogValue<T> => T.Write(in value, ref this, field);

    /// <summary>The enum's underlying value plus a reference to its name table (resolved on the consumer, never <c>ToString()</c>).</summary>
    public void WriteEnum<TEnum>(ushort field, TEnum value) where TEnum : unmanaged, Enum
    {
        int names = _storage?.AddReference(LogEnumNames<TEnum>.Instance) ?? -1;
        if (!TryReserve(field, LogValueKind.Enum, 10, out Span<byte> d)) return;
        BinaryPrimitives.WriteInt64LittleEndian(d, LogEnumNames<TEnum>.ToInt64(value));
        BinaryPrimitives.WriteInt16LittleEndian(d[8..], (short)(names is >= 0 and <= short.MaxValue ? names : -1));
    }

    /// <summary>Formats as UTF-8 on the caller (costs the formatting; analyzer YFL0010 points this out).</summary>
    public void WriteFormatted<T>(ushort field, in T value, scoped ReadOnlySpan<char> format = default) where T : IUtf8SpanFormattable
    {
        if (!TryReserveVariable(field, LogValueKind.Utf8, out int start)) return;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Span<byte> d = _buffer[(start + HeaderSize + 3)..];
            int room = Math.Min(d.Length, MaxTextCharacters * 2);
            if (value.TryFormat(d[..room], out int written, format, CultureInfo.InvariantCulture))
            {
                CommitVariable(start, LogTextFlags.None, written, written);
                return;
            }
            if (attempt == 0 && !TryGrowFor(start, 3 + MaxTextCharacters * 2)) break;
        }
        CommitOmitted(start, LogOmission.TooLarge);
    }

    /// <summary>Formats as UTF-16 text on the caller (value objects that implement <see cref="ISpanFormattable"/>).</summary>
    public void WriteSpanFormattable<T>(ushort field, in T value, scoped ReadOnlySpan<char> format = default) where T : ISpanFormattable
    {
        if (!TryReserveVariable(field, LogValueKind.Text, out int start)) return;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            Span<char> d = MemoryMarshal.Cast<byte, char>(_buffer[(start + HeaderSize + 3)..]);
            int room = Math.Min(d.Length, MaxTextCharacters);
            if (value.TryFormat(d[..room], out int written, format, CultureInfo.InvariantCulture))
            {
                CommitVariable(start, LogTextFlags.None, written, written * 2);
                return;
            }
            if (attempt == 0 && !TryGrowFor(start, 3 + MaxTextCharacters * 2)) break;
        }
        CommitOmitted(start, LogOmission.TooLarge);
    }

    /// <summary>A collection size: collections are captured as counts by default.</summary>
    public void WriteCount(ushort field, int count)
    {
        if (TryReserve(field, LogValueKind.Count, 4, out Span<byte> d)) BinaryPrimitives.WriteInt32LittleEndian(d, count);
    }

    /// <summary>A blob/stream length: content is never captured.</summary>
    public void WriteLength(ushort field, long length)
    {
        if (TryReserve(field, LogValueKind.Length, 8, out Span<byte> d)) BinaryPrimitives.WriteInt64LittleEndian(d, length);
    }

    /// <summary>Records that a value existed without recording it (secret-looking members, <c>[NoLog]</c>).</summary>
    public void WriteErased(ushort field) => WriteOmitted(field, LogOmission.Erased);

    /// <summary>Records that a value existed and why it was not recorded.</summary>
    public void WriteOmitted(ushort field, LogOmission reason)
    {
        if (TryReserve(field, LogValueKind.Omitted, 1, out Span<byte> d)) d[0] = (byte)reason;
    }

    // ── Nested objects ───────────────────────────────────────────────────────

    /// <summary>
    /// Writes a record/class through its generated log shape (<see cref="LogShapeFor{T}"/>). Nested deeper than
    /// <see cref="MaxDepth"/>, or without a shape, writes <see cref="LogValueKind.Omitted"/>.
    /// </summary>
    public void WriteShape<T>(ushort field, in T value)
    {
        if (value is null)
        {
            WriteNull(field);
            return;
        }
        LogShapeWriter<T>? write = LogShapeFor<T>.Write;
        if (write is null)
        {
            WriteOmitted(field, LogOmission.NoShape);
            return;
        }
        if (_depth >= MaxDepth)
        {
            WriteOmitted(field, LogOmission.DepthLimit);
            return;
        }
        if (!BeginObject(field, LogShapeFor<T>.Names)) return;
        write(in value, ref this);
        EndObject();
    }

    /// <summary>
    /// Opens a nested object whose fields index <paramref name="names"/> (null: fields use <see cref="Field(string)"/> names).
    /// Returns false (nothing opened) when the record is full or too deep.
    /// </summary>
    public bool BeginObject(ushort field, string[]? names = null)
    {
        if (_depth >= MaxDepth) return false;
        int reference = names is null ? -1 : _storage?.AddReference(names) ?? -1;
        if (!TryReserve(field, LogValueKind.ObjectStart, 2, out Span<byte> d)) return false;
        BinaryPrimitives.WriteInt16LittleEndian(d, (short)(reference is >= 0 and <= short.MaxValue ? reference : -1));
        _depth++;
        return true;
    }

    /// <summary>Closes the innermost object opened by <see cref="BeginObject"/>.</summary>
    public void EndObject()
    {
        if (_depth == 0) return;
        _depth--;
        // Always try to close, even after truncation, so readers see balanced objects whenever space allows.
        if (_position + HeaderSize > _buffer.Length && !TryGrow(HeaderSize)) return;
        _buffer[_position] = 0;
        _buffer[_position + 1] = 0;
        _buffer[_position + 2] = (byte)LogValueKind.ObjectEnd;
        _position += HeaderSize;
        _fieldCount++;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReserve(ushort field, LogValueKind kind, int payload, out Span<byte> destination)
    {
        int needed = HeaderSize + payload;
        if (_truncated || field == NoField || _fieldCount >= MaxFieldsPerRecord
            || (_position + needed > _buffer.Length && !TryGrow(needed)))
        {
            _truncated = true;
            destination = default;
            return false;
        }

        Span<byte> entry = _buffer.Slice(_position, needed);
        BinaryPrimitives.WriteUInt16LittleEndian(entry, field);
        entry[2] = (byte)kind;
        destination = entry[HeaderSize..];
        _position += needed;
        _fieldCount++;
        return true;
    }

    private void WriteText(ushort field, scoped ReadOnlySpan<char> value, int maxCharacters, LogTextFlags flags)
    {
        int chars = Math.Min(value.Length, Math.Max(maxCharacters, 0));
        if (chars < value.Length) flags |= LogTextFlags.Truncated;
        int needed = 3 + chars * 2;

        // Fit what we can rather than drop the whole value: text is where long inputs live.
        if (!_truncated && field != NoField && _position + HeaderSize + needed > _buffer.Length && !TryGrow(HeaderSize + needed))
        {
            int room = (_buffer.Length - _position - HeaderSize - 3) / 2;
            if (room <= 0)
            {
                _truncated = true;
                return;
            }
            chars = room;
            flags |= LogTextFlags.Truncated;
            needed = 3 + chars * 2;
        }

        if (TryReserve(field, LogValueKind.Text, needed, out Span<byte> d)) WriteTextPayload(d, value[..chars], flags);
    }

    private static void WriteTextPayload(Span<byte> destination, ReadOnlySpan<char> text, LogTextFlags flags)
    {
        destination[0] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[1..], (ushort)text.Length);
        MemoryMarshal.AsBytes(text).CopyTo(destination[3..]);
    }

    private static void WriteDecimal(Span<byte> destination, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteInt32LittleEndian(destination[(i * 4)..], bits[i]);
    }

    // Variable-length entries (formatted values): reserve the header + text header, format in place, then commit.
    private bool TryReserveVariable(ushort field, LogValueKind kind, out int start)
    {
        start = _position;
        if (!TryReserve(field, kind, 3, out _)) return false;
        _position = start;   // CommitVariable / CommitOmitted set the final position
        return true;
    }

    private void CommitVariable(int start, LogTextFlags flags, int length, int byteCount)
    {
        Span<byte> d = _buffer[(start + HeaderSize)..];
        d[0] = (byte)flags;
        BinaryPrimitives.WriteUInt16LittleEndian(d[1..], (ushort)length);
        _position = start + HeaderSize + 3 + byteCount;
    }

    private void CommitOmitted(int start, LogOmission reason)
    {
        _buffer[start + 2] = (byte)LogValueKind.Omitted;
        _buffer[start + HeaderSize] = (byte)reason;
        _position = start + HeaderSize + 1;
    }

    private bool TryGrowFor(int start, int required)
        => _buffer.Length - start - HeaderSize >= required || TryGrowFrom(start + HeaderSize, required);

    private bool TryGrow(int required) => TryGrowFrom(_position, required);

    private bool TryGrowFrom(int written, int required)
    {
        if (_storage is null || _grown) return false;
        _grown = true;
        Span<byte> larger = _storage.Grow(written, required);
        if (larger.Length < written + required) return false;
        _buffer = larger;
        return true;
    }
}
