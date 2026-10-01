using System.Buffers;
using System.Globalization;
using System.Text.Json;
using YFex.Extensions;

namespace YFex.Rules;

/// <summary>Writes one value as JSON (codecs for cells, rows and parameters — no reflection, no STJ contracts).</summary>
public delegate void JsonWriteFn<T>(Utf8JsonWriter writer, T value);
/// <summary>Reads one value; the reader is positioned ON the value token.</summary>
public delegate T JsonReadFn<T>(ref Utf8JsonReader reader);
/// <summary>Writes a row's cells as the properties of an already-open JSON object.</summary>
public delegate void RowWriteFn<TRow>(Utf8JsonWriter writer, in TRow row);
/// <summary>Reads a row from its <c>"cells"</c> object (reader positioned on StartObject).</summary>
public delegate TRow RowReadFn<TRow>(ref Utf8JsonReader reader);

/// <summary>
/// JSON of scalars supported by <see cref="RawValueConvert"/>: numbers (and any <see cref="IRawValue{TSelf}"/> type as its
/// storage — Money as its amount, Mass in grams, a text-backed id as its text), bool, text, ISO dates/times, Guids.
/// Enums use <see cref="EnumJson{TEnum}"/> (stable codes).
/// </summary>
public static class JsonScalar
{
    public static void Write<T>(Utf8JsonWriter w, T value)
    {
        var raw = RawValueConvert.From(value);
        switch (raw.Kind)
        {
            case ValueKind.None: w.WriteNullValue(); break;
            case ValueKind.Bool: raw.TryGet(out bool b); w.WriteBooleanValue(b); break;
            case ValueKind.Text: raw.TryGet(out string? s); w.WriteStringValue(s); break;
            case ValueKind.Date: raw.TryGet(out DateOnly d); w.WriteStringValue(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case ValueKind.Time: raw.TryGet(out TimeOnly t); w.WriteStringValue(t.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture)); break;
            case ValueKind.DateTime: raw.TryGet(out DateTime dt); w.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture)); break;
            case ValueKind.Guid: raw.TryGet(out Guid g); w.WriteStringValue(g); break;
            case ValueKind.Double: raw.TryGet(out double dd); w.WriteNumberValue(dd); break;
            default:
                if (!raw.TryGet(out decimal m)) throw new NotSupportedException($"No JSON form for {typeof(T).Name}.");
                w.WriteNumberValue(m);
                break;
        }
    }

    public static T Read<T>(ref Utf8JsonReader r)
    {
        RawValue raw;
        switch (r.TokenType)
        {
            case JsonTokenType.Null: return default!;
            case JsonTokenType.True: raw = RawValue.From(true); break;
            case JsonTokenType.False: raw = RawValue.From(false); break;
            case JsonTokenType.Number:
                if (typeof(T) == typeof(int) || typeof(T) == typeof(long) || typeof(T) == typeof(short) || typeof(T) == typeof(byte)) raw = RawValue.From(r.GetInt64());
                else if (typeof(T) == typeof(double) || typeof(T) == typeof(float)) raw = RawValue.From(r.GetDouble());
                else if (typeof(T) != typeof(decimal) && r.TryGetInt64(out long whole)) raw = RawValue.From(whole);   // Int64-stored app types
                else raw = RawValue.From(r.GetDecimal());
                break;
            case JsonTokenType.String:
                string text = r.GetString()!;
                if (typeof(T) == typeof(DateOnly)) raw = RawValue.From(DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture));
                else if (typeof(T) == typeof(TimeOnly)) raw = RawValue.From(TimeOnly.Parse(text, CultureInfo.InvariantCulture));
                else if (typeof(T) == typeof(DateTime)) raw = RawValue.From(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                else if (typeof(T) == typeof(Guid)) raw = RawValue.From(Guid.Parse(text));
                else raw = RawValue.From(text);
                break;
            default: throw new JsonException($"Unexpected {r.TokenType} for {typeof(T).Name}.");
        }
        return RawValueConvert.TryGet(in raw, out T value) ? value : throw new JsonException($"Cannot read {raw.Kind} as {typeof(T).Name}.");
    }
}

/// <summary>Enum JSON as the stable <c>[EnumCode]</c> (or member name) — renames don't break stored rows.</summary>
public static class EnumJson<TEnum> where TEnum : struct, Enum
{
    public static readonly JsonWriteFn<TEnum> Write = static (w, v) => w.WriteStringValue(v.GetCode());
    public static readonly JsonReadFn<TEnum> Read = static (ref Utf8JsonReader r) => EnumExtensions.FromCode<TEnum>(r.GetString()!);
}

/// <summary>JSON of decision-table cells and row documents (05 §2 <c>RowsJson</c>; generated rows call these).</summary>
public static class TableJson
{
    // Match: null = Any | {"is":v} | {"not":v} | {"in":[v, …]}
    public static void WriteMatch<T>(Utf8JsonWriter w, in Match<T> cell, JsonWriteFn<T> write)
    {
        if (cell.Kind == MatchKind.Any) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        switch (cell.Kind)
        {
            case MatchKind.Is: w.WritePropertyName("is"); write(w, cell.Value); break;
            case MatchKind.Not: w.WritePropertyName("not"); write(w, cell.Value); break;
            default:
                w.WriteStartArray("in");
                foreach (var v in cell.Values) write(w, v);
                w.WriteEndArray();
                break;
        }
        w.WriteEndObject();
    }

    public static Match<T> ReadMatch<T>(ref Utf8JsonReader r, JsonReadFn<T> read)
    {
        if (r.TokenType == JsonTokenType.Null) return Match<T>.Any;
        Expect(ref r, JsonTokenType.StartObject);
        Match<T> result = Match<T>.Any;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            string name = r.GetString()!;
            r.Read();
            switch (name)
            {
                case "is": result = Match<T>.Is(read(ref r)); break;
                case "not": result = Match<T>.Not(read(ref r)); break;
                case "in":
                    Expect(ref r, JsonTokenType.StartArray);
                    var values = new List<T>();
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray) values.Add(read(ref r));
                    result = Match<T>.In(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));
                    break;
                default: r.Skip(); break;
            }
        }
        return result;
    }

    // Band: null = Any | {"from":v, "below":v} (either side optional, half-open [from, below))
    public static void WriteBand<T>(Utf8JsonWriter w, in Band<T> cell, JsonWriteFn<T> write) where T : IComparable<T>
    {
        if (cell.IsAny) { w.WriteNullValue(); return; }
        w.WriteStartObject();
        if (cell.HasFrom) { w.WritePropertyName("from"); write(w, cell.FromValue); }
        if (cell.HasTo) { w.WritePropertyName("below"); write(w, cell.ToValue); }
        w.WriteEndObject();
    }

    public static Band<T> ReadBand<T>(ref Utf8JsonReader r, JsonReadFn<T> read) where T : IComparable<T>
    {
        if (r.TokenType == JsonTokenType.Null) return Band<T>.Any;
        Expect(ref r, JsonTokenType.StartObject);
        bool hasFrom = false, hasTo = false;
        T from = default!, to = default!;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            string name = r.GetString()!;
            r.Read();
            if (name == "from") { from = read(ref r); hasFrom = true; }
            else if (name == "below") { to = read(ref r); hasTo = true; }
            else r.Skip();
        }
        return (hasFrom, hasTo) switch
        {
            (true, true) => Band<T>.Between(from, to),
            (true, false) => Band<T>.From(from),
            (false, true) => Band<T>.Below(to),
            _ => Band<T>.Any,
        };
    }

    /// <summary>
    /// Row document: <c>{"rows":[{"id":"…","priority":0,"sealed":false,"from":"…","to":"…","note":"…","cells":{…}}, …]}</c>.
    /// Empty <paramref name="meta"/> → no metadata written (ids are then assigned when the version is created).
    /// </summary>
    public static byte[] Write<TRow>(ReadOnlySpan<TRow> rows, ReadOnlySpan<RowMeta> meta, RowWriteFn<TRow> writeRow)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("rows");
            for (int i = 0; i < rows.Length; i++)
            {
                w.WriteStartObject();
                if (!meta.IsEmpty)
                {
                    var m = meta[i];
                    w.WriteString("id", m.RowId);
                    if (m.Priority != 0) w.WriteNumber("priority", m.Priority);
                    if (m.Sealed) w.WriteBoolean("sealed", true);
                    if (m.EffectiveFrom is { } f) w.WriteString("from", f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (m.EffectiveTo is { } t) w.WriteString("to", t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    if (m.Note is { } n) w.WriteString("note", n);
                }
                w.WriteStartObject("cells");
                writeRow(w, in rows[i]);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a row document. Rows without an id get <see cref="Guid.Empty"/> (the caller assigns ids).</summary>
    public static (TRow[] Rows, RowMeta[] Meta) Read<TRow>(ReadOnlySpan<byte> json, RowReadFn<TRow> readRow)
    {
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var rows = new List<TRow>();
        var meta = new List<RowMeta>();
        r.Read();
        Expect(ref r, JsonTokenType.StartObject);
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.GetString() != "rows") { r.Read(); r.Skip(); continue; }
            r.Read();
            Expect(ref r, JsonTokenType.StartArray);
            while (r.Read() && r.TokenType == JsonTokenType.StartObject)
            {
                Guid id = Guid.Empty;
                int priority = 0;
                bool isSealed = false;
                DateOnly? from = null, to = null;
                string? note = null;
                TRow row = default!;
                bool hasCells = false;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    string name = r.GetString()!;
                    r.Read();
                    switch (name)
                    {
                        case "id": id = r.GetGuid(); break;
                        case "priority": priority = r.GetInt32(); break;
                        case "sealed": isSealed = r.GetBoolean(); break;
                        case "from": from = DateOnly.ParseExact(r.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                        case "to": to = DateOnly.ParseExact(r.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                        case "note": note = r.GetString(); break;
                        case "cells": row = readRow(ref r); hasCells = true; break;
                        default: r.Skip(); break;
                    }
                }
                if (!hasCells) throw new JsonException($"Row {rows.Count} has no \"cells\".");
                rows.Add(row);
                meta.Add(new RowMeta(id, priority, isSealed, from, to, note));
            }
        }
        return ([.. rows], [.. meta]);
    }

    /// <summary>Generated <c>ReadRow</c> helper: iterates the properties of a cells object.</summary>
    public static bool NextProperty(ref Utf8JsonReader r, out string name)
    {
        if (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            name = r.GetString()!;
            r.Read();
            return true;
        }
        name = "";
        return false;
    }

    internal static void Expect(ref Utf8JsonReader r, JsonTokenType type)
    {
        if (r.TokenType != type) throw new JsonException($"Expected {type}, found {r.TokenType}.");
    }
}
