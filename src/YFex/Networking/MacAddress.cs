using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using YFex.Privacy;

namespace YFex.Networking;

/// <summary>
/// A 48-bit IEEE 802 MAC address stored in the low 48 bits of a <c>ulong</c> (networking ⚑N3: moved from YFex.Devices to core).
/// <para>
/// Formats: <c>"C"</c> (default) <c>00:1A:2B:3C:4D:5E</c>, <c>"D"</c> <c>00-1A-2B-3C-4D-5E</c> (Windows), <c>"N"</c>
/// <c>001A2B3C4D5E</c>; the lower-case letters (<c>"c"</c>, <c>"d"</c>, <c>"n"</c>) write lower-case hex. Parsing accepts <c>:</c> or
/// <c>-</c> separated pairs, 12 contiguous hex digits and the Cisco form <c>001a.2b3c.4d5e</c>, in either case, UTF-16 or UTF-8.
/// </para>
/// <para>
/// Personal data (LGPD: a MAC can identify a person's device): masks and logs show only the vendor prefix, <c>00:1A:2B:**:**:**</c>.
/// <see cref="IClassifiedData{TSelf}"/> is written by hand here because YFex core does not run the Privacy generator (⚑N16).
/// </para>
/// </summary>
[Sensitive(DataSensitivity.Personal, Category = "identification", Mask = MaskPattern)]
[JsonConverter(typeof(MacAddressJsonConverter))]
public readonly struct MacAddress :
    IEquatable<MacAddress>,
    IComparable<MacAddress>,
    ISpanFormattable,
    IUtf8SpanFormattable,
    ISpanParsable<MacAddress>,
    IUtf8SpanParsable<MacAddress>,
    IEqualityOperators<MacAddress, MacAddress, bool>,
    IComparisonOperators<MacAddress, MacAddress, bool>,
    IClassifiedData<MacAddress>
{
    /// <summary>Size of the address in bytes.</summary>
    public const int Size = 6;

    /// <summary>Upper bound of characters (and UTF-8 bytes) written by <c>TryFormat</c>.</summary>
    public const int MaxFormattedLength = 17;

    private const string MaskPattern = "##:##:##:**:**:**";
    private const ulong AllBits = 0xFFFF_FFFF_FFFFUL;
    private const ulong MulticastBit = 1UL << 40;
    private const ulong LocallyAdministeredBit = 1UL << 41;
    private const int MaxParseLength = 64;

    private readonly ulong _value;

    /// <summary>Creates from the low 48 bits of <paramref name="value"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> uses more than 48 bits.</exception>
    public MacAddress(ulong value)
    {
        if (value > AllBits) throw new ArgumentOutOfRangeException(nameof(value), value, "A MAC address has 48 bits.");
        _value = value;
    }

    /// <summary>The address as a number (first byte in bits 47–40).</summary>
    public ulong Value => _value;

    /// <summary><c>FF:FF:FF:FF:FF:FF</c>.</summary>
    public static MacAddress Broadcast => new(AllBits);

    /// <summary>Vendor prefix: the organizationally unique identifier (OUI), the first 24 bits.</summary>
    public uint VendorPrefix => (uint)(_value >> 24);

    /// <summary>Group address bit (multicast and broadcast).</summary>
    public bool IsMulticast => (_value & MulticastBit) != 0;

    /// <summary>True for <c>FF:FF:FF:FF:FF:FF</c>.</summary>
    public bool IsBroadcast => _value == AllBits;

    /// <summary>Locally administered bit: randomized (phones, Windows "random hardware addresses") or virtual adapters.</summary>
    public bool IsLocallyAdministered => (_value & LocallyAdministeredBit) != 0;

    /// <summary>Creates from the 6 bytes in transmission order.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> is not exactly <see cref="Size"/> bytes.</exception>
    public static MacAddress FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size) throw new ArgumentException($"A MAC address has {Size} bytes; got {bytes.Length}.", nameof(bytes));
        return FromValidated(ReadValue(bytes));
    }

    /// <summary>Writes the 6 bytes in transmission order; false when <paramref name="destination"/> is shorter.</summary>
    public bool TryWriteBytes(Span<byte> destination)
    {
        if (destination.Length < Size) return false;
        Span<byte> full = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(full, _value);
        full[2..].CopyTo(destination);
        return true;
    }

    /// <summary>From a BCL <see cref="PhysicalAddress"/> (e.g. <c>NetworkInterface.GetPhysicalAddress()</c>); false when it is not 6 bytes
    /// (tunnel adapters report 0 or 8 bytes).</summary>
    public static bool TryFromPhysicalAddress(PhysicalAddress? address, out MacAddress result)
    {
        result = default;
        if (address is null) return false;
        byte[] bytes = address.GetAddressBytes();     // the BCL has no span overload (checked .NET 11 preview 4)
        if (bytes.Length != Size) return false;
        result = FromValidated(ReadValue(bytes));
        return true;
    }

    /// <summary>From a BCL <see cref="PhysicalAddress"/>.</summary>
    /// <exception cref="ArgumentException">The address is not 6 bytes.</exception>
    public static MacAddress FromPhysicalAddress(PhysicalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return TryFromPhysicalAddress(address, out MacAddress result)
            ? result
            : throw new ArgumentException("The physical address is not a 6-byte MAC address.", nameof(address));
    }

    /// <summary>To a BCL <see cref="PhysicalAddress"/> (allocates).</summary>
    public PhysicalAddress ToPhysicalAddress()
    {
        byte[] bytes = new byte[Size];
        TryWriteBytes(bytes);
        return new PhysicalAddress(bytes);
    }

    // ── equality and order ───────────────────────────────────────────────────────────────────────────────────────────────

    public bool Equals(MacAddress other) => _value == other._value;
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is MacAddress other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public int CompareTo(MacAddress other) => _value.CompareTo(other._value);

    public static bool operator ==(MacAddress left, MacAddress right) => left._value == right._value;
    public static bool operator !=(MacAddress left, MacAddress right) => left._value != right._value;
    public static bool operator <(MacAddress left, MacAddress right) => left._value < right._value;
    public static bool operator <=(MacAddress left, MacAddress right) => left._value <= right._value;
    public static bool operator >(MacAddress left, MacAddress right) => left._value > right._value;
    public static bool operator >=(MacAddress left, MacAddress right) => left._value >= right._value;

    public static explicit operator ulong(MacAddress value) => value._value;
    public static explicit operator MacAddress(ulong value) => new(value);

    // ── formatting ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>00:1A:2B:3C:4D:5E</c>.</summary>
    public override string ToString() => ToString(null, null);

    /// <summary>Formats with <c>"C"</c>, <c>"D"</c> or <c>"N"</c> (lower-case letter = lower-case hex).</summary>
    /// <exception cref="FormatException">Unknown format.</exception>
    public string ToString(string? format, IFormatProvider? formatProvider = null)
    {
        Span<char> buffer = stackalloc char[MaxFormattedLength];
        TryFormat(buffer, out int written, format);
        return new string(buffer[..written]);
    }

    /// <inheritdoc cref="ToString(string?, IFormatProvider?)"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        (char separator, bool lowerCase) = ReadFormat(format);
        int length = separator == '\0' ? 2 * Size : MaxFormattedLength;
        if (destination.Length < length)
        {
            charsWritten = 0;
            return false;
        }

        int step = separator == '\0' ? 2 : 3;
        for (int i = 0; i < Size; i++)
        {
            int octet = (int)(_value >> (40 - 8 * i)) & 0xFF;
            int at = i * step;
            destination[at] = HexDigit(octet >> 4, lowerCase);
            destination[at + 1] = HexDigit(octet & 0xF, lowerCase);
            if (separator != '\0' && i < Size - 1) destination[at + 2] = separator;
        }
        charsWritten = length;
        return true;
    }

    /// <inheritdoc cref="ToString(string?, IFormatProvider?)"/>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        Span<char> chars = stackalloc char[MaxFormattedLength];
        TryFormat(chars, out int length, format);
        if (utf8Destination.Length < length)
        {
            bytesWritten = 0;
            return false;
        }
        for (int i = 0; i < length; i++) utf8Destination[i] = (byte)chars[i];   // ASCII only
        bytesWritten = length;
        return true;
    }

    // ── parsing ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    public static bool TryParse(ReadOnlySpan<char> s, out MacAddress result)
    {
        result = default;
        s = s.Trim();
        ulong value = 0;
        switch (s.Length)
        {
            case 12:                                                    // 001A2B3C4D5E
                if (!TryAccumulateHex(s, ref value)) return false;
                break;

            case 14:                                                    // 001a.2b3c.4d5e (Cisco)
                if (s[4] != '.' || s[9] != '.') return false;
                if (!TryAccumulateHex(s[..4], ref value) || !TryAccumulateHex(s.Slice(5, 4), ref value)
                    || !TryAccumulateHex(s.Slice(10, 4), ref value)) return false;
                break;

            case MaxFormattedLength:                                    // 00:1A:2B:3C:4D:5E or 00-1A-2B-3C-4D-5E
                char separator = s[2];
                if (separator is not (':' or '-')) return false;
                for (int i = 0; i < Size; i++)
                {
                    int at = i * 3;
                    if (i < Size - 1 && s[at + 2] != separator) return false;
                    if (!TryAccumulateHex(s.Slice(at, 2), ref value)) return false;
                }
                break;

            default:
                return false;
        }
        result = FromValidated(value);
        return true;
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8Text, out MacAddress result)
    {
        result = default;
        if (utf8Text.Length > MaxParseLength) return false;
        Span<char> chars = stackalloc char[utf8Text.Length];
        for (int i = 0; i < utf8Text.Length; i++)
        {
            byte b = utf8Text[i];
            if (b > 0x7F) return false;
            chars[i] = (char)b;
        }
        return TryParse(chars, out result);
    }

    public static bool TryParse([NotNullWhen(true)] string? s, out MacAddress result) => TryParse(s.AsSpan(), out result);
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out MacAddress result) => TryParse(s, out result);
    public static bool TryParse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider, out MacAddress result) => TryParse(utf8Text, out result);
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out MacAddress result) => TryParse(s.AsSpan(), out result);

    public static MacAddress Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null)
        => TryParse(s, out MacAddress result) ? result : throw InvalidText(s.ToString());

    public static MacAddress Parse(ReadOnlySpan<byte> utf8Text, IFormatProvider? provider = null)
        => TryParse(utf8Text, out MacAddress result) ? result : throw InvalidText(System.Text.Encoding.UTF8.GetString(utf8Text));

    public static MacAddress Parse(string s, IFormatProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(s);
        return TryParse(s.AsSpan(), out MacAddress result) ? result : throw InvalidText(s);
    }

    // ── privacy (hand-written IClassifiedData, ⚑N16) ─────────────────────────────────────────────────────────────────────

    /// <summary>Personal data, category <c>identification</c>.</summary>
    public static DataClassification Classification { get; } = new(DataSensitivity.Personal, DataCategory.Identification);

    public static int MaxMaskedLength => MaxFormattedLength;

    /// <summary>Writes <c>00:1A:2B:**:**:**</c>: the vendor prefix stays visible (useful to support, not identifying).</summary>
    public static bool TryWriteMasked(in MacAddress value, Span<char> destination, out int written)
    {
        Span<char> text = stackalloc char[MaxFormattedLength];
        value.TryFormat(text, out int length);
        return Masks.TryWritePattern(text[..length], MaskPattern, destination, out written);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    private MacAddress(ulong value, bool _) => _value = value;

    /// <summary>For values already known to fit 48 bits (parsed or read from 6 bytes): skips the range check.</summary>
    private static MacAddress FromValidated(ulong value) => new(value, false);

    private static ulong ReadValue(ReadOnlySpan<byte> sixBytes)
    {
        Span<byte> padded = stackalloc byte[sizeof(ulong)];
        padded[..2].Clear();
        sixBytes.CopyTo(padded[2..]);
        return BinaryPrimitives.ReadUInt64BigEndian(padded);
    }

    private static (char Separator, bool LowerCase) ReadFormat(ReadOnlySpan<char> format)
    {
        if (format.IsEmpty) return (':', false);
        if (format.Length == 1)
        {
            switch (format[0])
            {
                case 'C': return (':', false);
                case 'c': return (':', true);
                case 'D': return ('-', false);
                case 'd': return ('-', true);
                case 'N': return ('\0', false);
                case 'n': return ('\0', true);
            }
        }
        throw new FormatException($"Unknown MacAddress format '{format.ToString()}'. Use C, D or N (lower case for lower-case hex).");
    }

    private static char HexDigit(int nibble, bool lowerCase)
        => (char)(nibble < 10 ? '0' + nibble : (lowerCase ? 'a' : 'A') + nibble - 10);

    private static bool TryAccumulateHex(ReadOnlySpan<char> digits, ref ulong value)
    {
        foreach (char c in digits)
        {
            int nibble = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'A' and <= 'F' => c - 'A' + 10,
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };
            if (nibble < 0) return false;
            value = (value << 4) | (uint)nibble;
        }
        return true;
    }

    private static FormatException InvalidText(string? text) => new($"'{text}' is not a valid MAC address.");
}

/// <summary>JSON: the <c>"C"</c> text as a string (also as a dictionary key). AOT-safe; declared on <see cref="MacAddress"/>.</summary>
public sealed class MacAddressJsonConverter : JsonConverter<MacAddress>
{
    public override MacAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected a JSON string for MacAddress.");
        return ReadText(ref reader);
    }

    public override MacAddress ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadText(ref reader);

    public override void Write(Utf8JsonWriter writer, MacAddress value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[MacAddress.MaxFormattedLength];
        value.TryFormat(buffer, out int written);
        writer.WriteStringValue(buffer[..written]);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, MacAddress value, JsonSerializerOptions options)
    {
        Span<byte> buffer = stackalloc byte[MacAddress.MaxFormattedLength];
        value.TryFormat(buffer, out int written);
        writer.WritePropertyName(buffer[..written]);
    }

    private static MacAddress ReadText(ref Utf8JsonReader reader)
    {
        if (!reader.HasValueSequence && !reader.ValueIsEscaped && MacAddress.TryParse(reader.ValueSpan, out MacAddress fast)) return fast;
        return MacAddress.TryParse(reader.GetString(), out MacAddress result)
            ? result
            : throw new JsonException("Invalid MAC address.");
    }
}
