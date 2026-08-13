using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace YFex.Security.ApiReverser;

/// <summary>
/// [PERSONAL USE] Decodes raw protobuf wire format without a .proto schema into a JSON
/// tree keyed by field number. Length-delimited fields are heuristically interpreted as
/// nested messages, UTF-8 strings, or raw hex.
/// </summary>
public static class ProtobufDecoder
{
    public static JsonDocument DecodeRaw(ReadOnlySpan<byte> protobufBytes)
    {
        var dict = DecodeToDictionary(protobufBytes, depth: 0);
        string json = JsonSerializer.Serialize(dict);
        return JsonDocument.Parse(json);
    }

    private static Dictionary<string, object?> DecodeToDictionary(ReadOnlySpan<byte> data, int depth)
    {
        var result = new Dictionary<string, object?>();
        int pos = 0;

        while (pos < data.Length)
        {
            if (!TryReadVarint(data, ref pos, out ulong tag))
                break;

            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 0x07);
            string key = fieldNumber.ToString();

            object? value;

            switch (wireType)
            {
                case 0: // Varint
                    if (!TryReadVarint(data, ref pos, out ulong varint)) return result;
                    value = varint;
                    break;

                case 1: // 64-bit
                    if (pos + 8 > data.Length) return result;
                    value = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(pos, 8));
                    pos += 8;
                    break;

                case 2: // Length-delimited
                    if (!TryReadVarint(data, ref pos, out ulong len)) return result;
                    int length = (int)len;
                    if (pos + length > data.Length) return result;
                    var sub = data.Slice(pos, length);
                    pos += length;
                    value = InterpretLengthDelimited(sub, depth);
                    break;

                case 5: // 32-bit
                    if (pos + 4 > data.Length) return result;
                    value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
                    pos += 4;
                    break;

                default:
                    return result; // unknown wire type — stop
            }

            // Repeated fields collapse into a list.
            if (result.TryGetValue(key, out var existing))
            {
                if (existing is List<object?> list) list.Add(value);
                else result[key] = new List<object?> { existing, value };
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static object? InterpretLengthDelimited(ReadOnlySpan<byte> data, int depth)
    {
        // 1. Try recursive protobuf (only if it consumes the whole buffer cleanly).
        if (depth < 8 && LooksLikeProtobuf(data))
        {
            var nested = DecodeToDictionary(data, depth + 1);
            if (nested.Count > 0) return nested;
        }

        // 2. Try UTF-8 string.
        if (IsLikelyUtf8(data))
            return Encoding.UTF8.GetString(data);

        // 3. Fall back to hex.
        return "0x" + Convert.ToHexStringLower(data);
    }

    private static bool LooksLikeProtobuf(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return false;

        int pos = 0;
        int fieldsRead = 0;

        while (pos < data.Length && fieldsRead < 4)
        {
            if (!TryReadVarint(data, ref pos, out ulong tag)) return false;
            int wireType = (int)(tag & 0x07);
            int fieldNumber = (int)(tag >> 3);
            if (fieldNumber == 0) return false;

            switch (wireType)
            {
                case 0:
                    if (!TryReadVarint(data, ref pos, out _)) return false;
                    break;
                case 1:
                    pos += 8;
                    break;
                case 2:
                    if (!TryReadVarint(data, ref pos, out ulong len)) return false;
                    pos += (int)len;
                    break;
                case 5:
                    pos += 4;
                    break;
                default:
                    return false;
            }
            fieldsRead++;
            if (pos > data.Length) return false;
        }

        return pos == data.Length;
    }

    private static bool IsLikelyUtf8(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;
        int printable = 0;
        foreach (byte b in data)
        {
            if (b == 0) return false;
            if (b >= 0x20 && b < 0x7f) printable++;
            else if (b is 0x09 or 0x0a or 0x0d) printable++;
        }
        return printable >= data.Length * 0.85;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift < 64)
        {
            byte b = data[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }
}
