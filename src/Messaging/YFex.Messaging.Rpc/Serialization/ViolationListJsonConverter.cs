using System.Text.Json;
using System.Text.Json.Serialization;
using YFex.Validation;

namespace YFex.Messaging.Rpc.Serialization;

/// <summary>
/// Lossless, reflection-free JSON for <see cref="ViolationList"/> (persisted sync failures, G7). Compact shape per
/// violation: <c>{"t":"Lines[].Quantity","i":[3],"c":"stock.out","s":2,"a":[[kind,low,high,"text",aux]],"o":[source,owner],"d":"detail"}</c>
/// (optional members omitted). Human-facing text is produced from code + args by the message catalog, never stored.
/// </summary>
public sealed class ViolationListJsonConverter : JsonConverter<ViolationList>
{
    public override ViolationList Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ViolationList.Empty;
        Expect(ref reader, JsonTokenType.StartArray);
        var items = new List<Violation>();
        Span<int> indices = stackalloc int[IndexPath.MaxDepth];
        while (Next(ref reader) != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Violation object expected.");
            string? template = null, code = null, detail = null;
            int depth = 0;
            var severity = Severity.Error;
            RawValue a0 = default, a1 = default;
            int argCount = 0;
            ViolationOrigin origin = default;
            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                string name = reader.GetString()!;
                Next(ref reader);
                switch (name)
                {
                    case "t": template = reader.GetString(); break;
                    case "c": code = reader.GetString(); break;
                    case "d": detail = reader.GetString(); break;
                    case "s": severity = (Severity)reader.GetByte(); break;
                    case "i":
                        while (Next(ref reader) != JsonTokenType.EndArray)
                        {
                            if (depth == IndexPath.MaxDepth) throw new JsonException("Too many indices.");
                            indices[depth++] = reader.GetInt32();
                        }
                        break;
                    case "a":
                        while (Next(ref reader) != JsonTokenType.EndArray)
                        {
                            var raw = ReadRaw(ref reader);
                            if (argCount == 0) a0 = raw; else if (argCount == 1) a1 = raw;
                            argCount++;
                        }
                        break;
                    case "o":
                        Next(ref reader); var source = (ViolationSource)reader.GetByte();
                        Next(ref reader); ushort owner = reader.GetUInt16();
                        Next(ref reader);   // EndArray
                        origin = new ViolationOrigin(source, owner);
                        break;
                    default: reader.Skip(); break;
                }
            }
            var path = template is null ? FieldPath.Empty : FieldPath.Of(template, IndexPath.Of(indices[..depth]));
            var args = argCount switch { 0 => RuleArgs.None, 1 => new RuleArgs(a0), _ => new RuleArgs(a0, a1) };
            items.Add(new Violation(in path, code is null ? default : new RuleCode(code), severity, in args, origin, detail));
        }
        return items.Count == 0 ? ViolationList.Empty : ViolationList.FromOwnedArray(items.ToArray());
    }

    public override void Write(Utf8JsonWriter writer, ViolationList value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (ref readonly var v in value)
        {
            writer.WriteStartObject();
            if (v.Path.Template is { } template) writer.WriteString("t", template);
            if (!v.Path.Indices.IsEmpty)
            {
                writer.WriteStartArray("i");
                for (int i = 0; i < v.Path.Indices.Count; i++) writer.WriteNumberValue(v.Path.Indices[i]);
                writer.WriteEndArray();
            }
            if (!v.Code.IsEmpty) writer.WriteString("c", v.Code.Value);
            writer.WriteNumber("s", (byte)v.Severity);
            if (v.Args.Count > 0)
            {
                writer.WriteStartArray("a");
                for (int i = 0; i < v.Args.Count; i++) WriteRaw(writer, v.Args[i]);
                writer.WriteEndArray();
            }
            if (v.Origin != default)
            {
                writer.WriteStartArray("o");
                writer.WriteNumberValue((byte)v.Origin.Source);
                writer.WriteNumberValue(v.Origin.OwnerId);
                writer.WriteEndArray();
            }
            if (v.Detail is { } detail) writer.WriteString("d", detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteRaw(Utf8JsonWriter writer, in RawValue value)
    {
        value.GetParts(out var kind, out ulong low, out ulong high, out string? text, out byte aux, out uint type);
        writer.WriteStartArray();
        writer.WriteNumberValue((byte)kind);
        writer.WriteNumberValue(low);
        writer.WriteNumberValue(high);
        if (text is null) writer.WriteNullValue(); else writer.WriteStringValue(text);
        writer.WriteNumberValue(aux);
        writer.WriteNumberValue(type);            // 6th element since RawType (arrays of 5 = legacy kinds)
        writer.WriteEndArray();
    }

    private static RawValue ReadRaw(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("RawValue array expected.");
        Next(ref reader); byte kind = reader.GetByte();
        Next(ref reader); ulong low = reader.GetUInt64();
        Next(ref reader); ulong high = reader.GetUInt64();
        Next(ref reader); string? text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
        Next(ref reader); byte aux = reader.GetByte();
        if (Next(ref reader) == JsonTokenType.EndArray) return RawValueLegacy.FromV1Parts(kind, low, high, text, aux);
        uint type = reader.GetUInt32();
        Next(ref reader);   // EndArray
        return RawValue.FromParts((ValueKind)kind, low, high, text, aux, type);
    }

    private static JsonTokenType Next(ref Utf8JsonReader reader)
        => reader.Read() ? reader.TokenType : throw new JsonException("Unexpected end of violations JSON.");

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type) throw new JsonException($"Expected {type}, got {reader.TokenType}.");
    }
}
