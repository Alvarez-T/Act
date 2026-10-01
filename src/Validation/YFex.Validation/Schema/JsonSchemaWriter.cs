using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using YFex.Localization;

namespace YFex.Validation;

/// <summary>Constraint lookup by member path (<c>T.TryGetConstraints</c>, <see cref="IValidationSource.TryGetConstraints"/>…).</summary>
public delegate bool ConstraintLookup(ReadOnlySpan<char> path, out FieldConstraints constraints);

/// <summary>Options of <see cref="JsonSchemaWriter"/>.</summary>
public sealed record JsonSchemaOptions
{
    public static JsonSchemaOptions Default { get; } = new();

    /// <summary>Field display names as <c>title</c> (key <c>&lt;TypeName&gt;.&lt;Path&gt;</c>, 10 §5).</summary>
    public IMessageCatalog? Catalog { get; init; }
    public CultureInfo Culture { get; init; } = CultureInfo.InvariantCulture;
    /// <summary>Nesting depth limit for recursive object graphs.</summary>
    public int MaxDepth { get; init; } = 8;
    /// <summary>Description of the whole object (tool description).</summary>
    public string? Description { get; init; }
}

/// <summary>
/// JSON Schema (draft 2020-12) of a validatable type: its generated <see cref="TypeShape"/> + its constraints — static
/// (<c>T.TryGetConstraints</c>) or live (an <see cref="IValidationSource"/>: a VM's static rules merged with the current
/// rules decision, so an AI tool sees "Installments ≤ 6" while that is the rule). No reflection.
/// <code>
/// string json = JsonSchemaWriter.ToJson&lt;CreateCustomer&gt;();                  // static
/// string live = JsonSchemaWriter.ToJson(SaleVm.Shape!, vm.TryGetConstraints);   // with the current decision
/// </code>
/// Mapping: Required → <c>required</c>; lengths → <c>minLength/maxLength</c> (arrays: <c>minItems/maxItems</c>); bounds →
/// <c>minimum/maximum</c> (dates: <c>formatMinimum/formatMaximum</c>); step → <c>multipleOf</c>; options → <c>enum</c>; format →
/// <c>format</c> (known: email, uri…) else <c>x-format</c>; app value types → <c>x-type</c>.
/// </summary>
public static class JsonSchemaWriter
{
    public static string ToJson<T>(JsonSchemaOptions? options = null) where T : IValidatable<T>
        => T.Shape is { } shape ? ToJson(shape, T.TryGetConstraints, options) : """{"type":"object"}""";

    /// <summary>
    /// Static rules merged with live ones (most restrictive per field) — e.g. a command's generated constraints with the current
    /// rules decision: <c>JsonSchemaWriter.Merge(CloseSale.TryGetConstraints, decision.TryGetConstraints)</c>.
    /// </summary>
    public static ConstraintLookup Merge(ConstraintLookup first, ConstraintLookup second)
        => (ReadOnlySpan<char> path, out FieldConstraints c) =>
        {
            bool a = first(path, out var x), b = second(path, out var y);
            c = a && b ? x.Merge(in y) : a ? x : y;
            return a || b;
        };

    public static string ToJson(TypeShape shape, ConstraintLookup? constraints = null, JsonSchemaOptions? options = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) Write(w, shape, constraints, options);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static void Write(Utf8JsonWriter w, TypeShape shape, ConstraintLookup? constraints = null, JsonSchemaOptions? options = null)
    {
        options ??= JsonSchemaOptions.Default;
        w.WriteStartObject();
        w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
        w.WriteString("title", shape.Name);
        if (options.Description is { } d) w.WriteString("description", d);
        WriteObjectBody(w, shape, "", constraints, options, 0);
        w.WriteEndObject();
    }

    private static void WriteObjectBody(Utf8JsonWriter w, TypeShape shape, string prefix, ConstraintLookup? constraints, JsonSchemaOptions options, int depth)
    {
        w.WriteString("type", "object");
        var required = new List<string>();
        w.WriteStartObject("properties");
        for (var s = shape; s is not null; s = s.Base?.Invoke())
            foreach (var m in s.Members)
            {
                string path = prefix.Length == 0 ? m.Name : prefix + "." + m.Name;
                FieldConstraints c = default;
                bool has = constraints is not null && constraints(path, out c);
                if (has && c.Required) required.Add(m.Name);
                w.WritePropertyName(m.Name);
                WriteMember(w, m, path, has ? c : default, shape.Name, constraints, options, depth);
            }
        w.WriteEndObject();
        if (required.Count > 0)
        {
            w.WriteStartArray("required");
            foreach (var r in required) w.WriteStringValue(r);
            w.WriteEndArray();
        }
        w.WriteBoolean("additionalProperties", false);
    }

    private static void WriteMember(Utf8JsonWriter w, MemberShape m, string path, in FieldConstraints c, string typeName,
        ConstraintLookup? constraints, JsonSchemaOptions options, int depth)
    {
        w.WriteStartObject();
        if (options.Catalog is { } catalog) w.WriteString("title", catalog.FieldName(typeName + "." + path, options.Culture));
        switch (m.Kind)
        {
            case JsonKind.Object when m.Nested?.Invoke() is { } nested && depth < options.MaxDepth:
                WriteNullable(w, "object", m.Nullable);
                w.WriteString("title", nested.Name);
                WriteObjectBodyInline(w, nested, path, constraints, options, depth + 1);
                break;
            case JsonKind.Array:
                WriteNullable(w, "array", m.Nullable);
                if (c.Has(ConstraintFlags.MinLength)) w.WriteNumber("minItems", c.MinLength);
                if (c.Has(ConstraintFlags.MaxLength)) w.WriteNumber("maxItems", c.MaxLength);
                if (m.Element is { } e)
                {
                    w.WritePropertyName("items");
                    // Element constraints are registered under "Path[]" by [Each] rules.
                    FieldConstraints ec = default;
                    bool hasElement = constraints is not null && constraints(path + "[]", out ec);
                    WriteMember(w, e, path + "[]", hasElement ? ec : default, typeName, constraints, options, depth + 1);
                }
                break;
            default:
                WriteScalar(w, m, in c);
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteObjectBodyInline(Utf8JsonWriter w, TypeShape nested, string path, ConstraintLookup? constraints, JsonSchemaOptions options, int depth)
    {
        // Nested members: the parent's lookup answers "Address.City" (generated TryGetConstraints delegates to the nested type).
        var required = new List<string>();
        w.WriteStartObject("properties");
        for (var s = nested; s is not null; s = s.Base?.Invoke())
            foreach (var m in s.Members)
            {
                string p = path + "." + m.Name;
                FieldConstraints c = default;
                bool has = constraints is not null && constraints(p, out c);
                if (has && c.Required) required.Add(m.Name);
                w.WritePropertyName(m.Name);
                WriteMember(w, m, p, has ? c : default, nested.Name, constraints, options, depth);
            }
        w.WriteEndObject();
        if (required.Count > 0)
        {
            w.WriteStartArray("required");
            foreach (var r in required) w.WriteStringValue(r);
            w.WriteEndArray();
        }
        w.WriteBoolean("additionalProperties", false);
    }

    private static void WriteScalar(Utf8JsonWriter w, MemberShape m, in FieldConstraints c)
    {
        string? format = null;
        string type = m.Kind switch
        {
            JsonKind.Bool => "boolean",
            JsonKind.Integer => "integer",
            JsonKind.Number => "number",
            JsonKind.Date => Fmt("string", "date", ref format),
            JsonKind.DateTime => Fmt("string", "date-time", ref format),
            JsonKind.Time => Fmt("string", "time", ref format),
            JsonKind.Guid => Fmt("string", "uuid", ref format),
            JsonKind.Any => "",
            _ => "string",
        };
        if (type.Length > 0) WriteNullable(w, type, m.Nullable);
        if (format is not null) w.WriteString("format", format);
        if (m.RawType is { IsPrimitive: false } raw) w.WriteString("x-type", raw.Key);

        if (m.Kind == JsonKind.Enum)
        {
            w.WriteStartArray("enum");
            foreach (var e in m.EnumMembers)
                if (!c.Has(ConstraintFlags.Options) || c.Options.Contains(e.Value)) w.WriteStringValue(e.Code);
            if (m.Nullable) w.WriteNullValue();
            w.WriteEndArray();
            return;
        }
        if (c.Has(ConstraintFlags.MinLength)) w.WriteNumber("minLength", c.MinLength);
        if (c.Has(ConstraintFlags.MaxLength)) w.WriteNumber("maxLength", c.MaxLength);
        bool dateLike = m.Kind is JsonKind.Date or JsonKind.DateTime or JsonKind.Time;
        if (c.Has(ConstraintFlags.Min)) WriteBound(w, dateLike ? "formatMinimum" : "minimum", c.Min);
        if (c.Has(ConstraintFlags.Max)) WriteBound(w, dateLike ? "formatMaximum" : "maximum", c.Max);
        if (c.Has(ConstraintFlags.Step) && c.Step.TryGet(out decimal step) && step > 0) w.WriteNumber("multipleOf", step);
        if (c.Has(ConstraintFlags.Options) && !c.Options.IsEmpty)
        {
            w.WriteStartArray("enum");
            foreach (var v in c.Options) WriteValue(w, v);
            w.WriteEndArray();
        }
        if (c.Has(ConstraintFlags.Format) && c.Format.Value is { Length: > 0 } f)
            w.WriteString(KnownFormats.Contains(f) ? "format" : "x-format", f);
    }

    private static readonly HashSet<string> KnownFormats = new(StringComparer.Ordinal) { "email", "uri", "hostname", "ipv4", "ipv6", "uuid", "date", "date-time", "time" };

    private static string Fmt(string type, string format, ref string? into) { into = format; return type; }

    private static void WriteNullable(Utf8JsonWriter w, string type, bool nullable)
    {
        if (!nullable) { w.WriteString("type", type); return; }
        w.WriteStartArray("type");
        w.WriteStringValue(type);
        w.WriteStringValue("null");
        w.WriteEndArray();
    }

    private static void WriteBound(Utf8JsonWriter w, string name, in RawValue v)
    {
        w.WritePropertyName(name);
        WriteValue(w, v);
    }

    private static void WriteValue(Utf8JsonWriter w, in RawValue v)
    {
        switch (v.Kind)
        {
            case ValueKind.Bool: v.TryGet(out bool b); w.WriteBooleanValue(b); break;
            case ValueKind.Int32 or ValueKind.Int64: v.TryGet(out long l); w.WriteNumberValue(l); break;
            case ValueKind.Decimal or ValueKind.Double: v.TryGet(out decimal d); w.WriteNumberValue(d); break;
            case ValueKind.Date: v.TryGet(out DateOnly date); w.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            case ValueKind.None: w.WriteNullValue(); break;
            default: v.TryGet(out string? s); w.WriteStringValue(s ?? v.ToString()); break;
        }
    }
}

/// <summary>
/// JSON Schemas of every validatable type, by CLR <see cref="Type"/> — filled by the generated module initializer
/// (<see cref="ValidationSlots.Register{T}"/>), so non-generic hosts (YFex.Capabilities, MCP servers) can describe a message
/// type without reflection.
/// </summary>
public static class JsonSchemas
{
    private static readonly ConcurrentDictionary<Type, Func<JsonSchemaOptions?, string>> s_writers = new();
    private static readonly ConcurrentDictionary<Type, (Func<TypeShape?> Shape, ConstraintLookup Constraints)> s_shapes = new();

    public static void Register<T>() where T : IValidatable<T>
    {
        s_writers[typeof(T)] = static o => JsonSchemaWriter.ToJson<T>(o);
        s_shapes[typeof(T)] = (static () => T.Shape, T.TryGetConstraints);
    }

    /// <summary>The generated shape + static constraints of <paramref name="type"/> (parameter lists without reflection).</summary>
    public static bool TryGetShape(Type type, out TypeShape shape, out ConstraintLookup constraints)
    {
        if (s_shapes.TryGetValue(type, out var entry) && entry.Shape() is { } s) { shape = s; constraints = entry.Constraints; return true; }
        shape = null!;
        constraints = null!;
        return false;
    }

    public static bool TryGet(Type type, out string schema, JsonSchemaOptions? options = null)
    {
        if (s_writers.TryGetValue(type, out var write)) { schema = write(options); return true; }
        schema = "";
        return false;
    }

    public static IEnumerable<Type> Types => s_writers.Keys;
}
