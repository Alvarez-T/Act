using System.Buffers;
using System.Text.Json;

namespace YFex.Expressions;

/// <summary>
/// Building blocks for authoring catalogs and JSON Schemas of documents that embed expressions (YFex.Rules.Dynamic rule specs,
/// Composer screens): the operator table, the function list and the expression <c>$defs</c> (<c>expr</c>, <c>var</c>, <c>line</c>,
/// <c>op</c>, <c>call</c>) specialized to a schema + catalog — usable as an LLM structured-output schema. Hosts write the envelope.
/// </summary>
public static class ExprAuthoring
{
    /// <summary>Every operator: JSON key, arity, description (catalog order).</summary>
    public static ReadOnlySpan<(string Op, string Arity, string Description)> Operators => s_operators;

    private static readonly (string Op, string Arity, string Description)[] s_operators =
    [
        ("+", "2+", "add numbers, or amounts of the same type"),
        ("-", "2+", "subtract numbers, or amounts of the same type"),
        ("*", "2+", "multiply numbers; an amount by a number"),
        ("/", "2+", "divide numbers; an amount by a number (amount / amount = ratio); division by zero gives null"),
        ("neg", "1", "negate"),
        ("==", "2", "equal (enum fields compare with their codes, e.g. \"Pix\")"),
        ("!=", "2", "not equal"),
        ("<", "2", "less than"), ("<=", "2", "less or equal"), (">", "2", "greater than"), (">=", "2", "greater or equal"),
        ("and", "1+", "all true"), ("or", "1+", "any true"), ("not", "1", "negation"),
        ("in", "2", "[value, [literal, …]] — value is one of the literals"),
        ("between", "3", "[value, low, high] — inclusive"),
        ("isSet", "1", "the value is not null"),
        ("if", "3", "[condition, then, else]"),
        ("coalesce", "2+", "first non-null"),
        ("sum", "1", "sum of a per-line expression over the lines (use {\"line\": …} inside)"),
        ("count", "1", "number of lines where a per-line condition holds"),
        ("any", "1", "some line satisfies the condition"), ("all", "1", "every line satisfies the condition"),
        ("minOf", "1", "smallest per-line value"), ("maxOf", "1", "largest per-line value"),
    ];

    private static readonly string[] ArrayOps = ["+", "-", "*", "/", "==", "!=", "<", "<=", ">", ">=", "and", "or", "between", "if", "coalesce"];
    private static readonly string[] SingleOps = ["neg", "not", "isSet", "sum", "count", "any", "all", "minOf", "maxOf"];

    // ── Catalog parts ───────────────────────────────────────────────────────

    /// <summary>Writes one object per operator (<c>op</c>, <c>arity</c>, <c>description</c>) into the current array.</summary>
    public static void WriteOperators(Utf8JsonWriter w)
    {
        foreach (var (op, arity, description) in s_operators)
        {
            w.WriteStartObject();
            w.WriteString("op", op);
            w.WriteString("arity", arity);
            w.WriteString("description", description);
            w.WriteEndObject();
        }
    }

    /// <summary>Writes one object per function (name, signature, description, category, implemented, deterministic) into the current array.</summary>
    public static void WriteFunctions(Utf8JsonWriter w, FunctionCatalog functions)
    {
        foreach (var f in Sorted(functions))
        {
            w.WriteStartObject();
            w.WriteString("name", f.Name);
            w.WriteString("signature", f.Signature);
            w.WriteString("description", f.Description);
            if (f.Category is { } c) w.WriteString("category", c);
            if (f.IsContract) w.WriteBoolean("implemented", false);
            if (!f.IsDeterministic) w.WriteBoolean("deterministic", false);
            w.WriteEndObject();
        }
    }

    /// <summary>Writes one object per special (<c>name</c>, <c>type</c>, <c>description</c>) into the current array.</summary>
    public static void WriteSpecial(Utf8JsonWriter w, string name, string type, string description)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("type", type);
        w.WriteString("description", description);
        w.WriteEndObject();
    }

    // ── JSON Schema $defs ───────────────────────────────────────────────────

    /// <summary>
    /// Writes the <c>expr</c>, <c>var</c>, <c>line</c> (when <paramref name="lineNames"/> is not null), <c>op</c> and <c>call</c> definitions
    /// into the current (<c>$defs</c>) object. <paramref name="vars"/>: field names + specials allowed in <c>{"var": …}</c>; calls list
    /// only implemented functions (and only deterministic ones when <paramref name="deterministicOnly"/>).
    /// </summary>
    public static void WriteExpressionDefs(Utf8JsonWriter w, IEnumerable<string> vars, IEnumerable<string>? lineNames, FunctionCatalog functions,
        bool deterministicOnly = false)
    {
        w.WriteStartObject("expr");
        w.WriteStartArray("anyOf");
        w.WriteStartObject(); w.WriteStartArray("type"); foreach (var t in new[] { "number", "string", "boolean", "null" }) w.WriteStringValue(t); w.WriteEndArray(); w.WriteEndObject();
        Ref(w, "var"); if (lineNames is not null) Ref(w, "line"); Ref(w, "op"); Ref(w, "call");
        w.WriteEndArray();
        w.WriteEndObject();

        SingleKey(w, "var", vars);
        if (lineNames is not null) SingleKey(w, "line", lineNames);

        w.WriteStartObject("op");
        w.WriteString("type", "object"); w.WriteNumber("minProperties", 1); w.WriteNumber("maxProperties", 1);
        w.WriteStartObject("properties");
        foreach (var op in ArrayOps) { w.WriteStartObject(op); ExprArray(w); w.WriteEndObject(); }
        foreach (var op in SingleOps) { w.WriteStartObject(op); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject(); }
        w.WriteStartObject("in");
        w.WriteString("type", "array");
        w.WriteStartArray("prefixItems");
        w.WriteStartObject(); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject();
        w.WriteStartObject(); w.WriteString("type", "array"); w.WriteEndObject();
        w.WriteEndArray();
        w.WriteNumber("minItems", 2); w.WriteNumber("maxItems", 2);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();

        w.WriteStartObject("call");
        w.WriteString("type", "object"); w.WriteNumber("minProperties", 1); w.WriteNumber("maxProperties", 1);
        w.WriteStartObject("properties");
        foreach (var f in Sorted(functions))
        {
            if (f.IsContract || (deterministicOnly && !f.IsDeterministic)) continue;
            w.WriteStartObject(f.Name);
            w.WriteString("description", $"{f.Signature} — {f.Description}");
            ExprArray(w, f.Parameters.Length);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
    }

    // ── Small JSON-Schema helpers (hosts reuse them for their envelopes) ────

    /// <summary><c>"type":"array","items":{"$ref":"#/$defs/expr"}</c> + item counts, inside the current object.</summary>
    public static void ExprArray(Utf8JsonWriter w, int? exactly = null, int? maxItems = null, int? minItems = null)
    {
        w.WriteString("type", "array");
        w.WriteStartObject("items"); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject();
        if (exactly is { } n) { w.WriteNumber("minItems", n); w.WriteNumber("maxItems", n); return; }
        if (minItems is { } min) w.WriteNumber("minItems", min);
        if (maxItems is { } max) w.WriteNumber("maxItems", max);
    }

    /// <summary>Property <paramref name="name"/> = an expression.</summary>
    public static void ExprProp(Utf8JsonWriter w, string name) { w.WriteStartObject(name); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject(); }

    /// <summary>Property <paramref name="name"/> = a string.</summary>
    public static void Str(Utf8JsonWriter w, string name) { w.WriteStartObject(name); w.WriteString("type", "string"); w.WriteEndObject(); }

    /// <summary>Array element <c>{"$ref":"#/$defs/<paramref name="def"/>"}</c>.</summary>
    public static void Ref(Utf8JsonWriter w, string def) { w.WriteStartObject(); w.WriteString("$ref", "#/$defs/" + def); w.WriteEndObject(); }

    /// <summary>Property <paramref name="name"/> = one of <paramref name="values"/>.</summary>
    public static void Enum(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartObject(name);
        w.WriteStartArray("enum");
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    /// <summary>Definition <paramref name="key"/> = an object with the single required key <paramref name="key"/> whose value is one of <paramref name="names"/>.</summary>
    public static void SingleKey(Utf8JsonWriter w, string key, IEnumerable<string> names)
    {
        w.WriteStartObject(key);
        w.WriteString("type", "object");
        w.WriteStartArray("required"); w.WriteStringValue(key); w.WriteEndArray();
        w.WriteStartObject("properties");
        Enum(w, key, names);
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
    }

    /// <summary>Functions ordered by name (stable catalogs and schemas).</summary>
    public static List<ExprFunction> Sorted(FunctionCatalog catalog)
    {
        var list = new List<ExprFunction>(catalog.All);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    /// <summary>Runs <paramref name="body"/> against a fresh writer and returns the UTF-16 JSON text.</summary>
    public static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) body(w);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
