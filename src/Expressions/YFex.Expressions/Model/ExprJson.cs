using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace YFex.Expressions;

/// <summary>
/// JSON form of <see cref="Expr"/> (hand-written reader/writer — AOT-safe, no STJ contracts). JsonLogic-like: an
/// operator/function name as the single key of an object, its operands as the value.
/// <code>
/// { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] }
/// { "sum": { "*": [ { "line": "UnitPrice" }, { "line": "Quantity" } ] } }
/// { "round": [ { "var": "Total" }, 2 ] }
/// </code>
/// Hosts embed expressions in their own documents (rule specs, screen definitions) and call <see cref="Read(ref Utf8JsonReader, string)"/>
/// / <see cref="Write"/> for each expression; paths in <see cref="JsonException"/>s are prefixed with the caller's path.
/// </summary>
public static class ExprJson
{
    private static readonly FrozenDictionary<string, ExprKind> ByName = new Dictionary<string, ExprKind>(StringComparer.Ordinal)
    {
        ["+"] = ExprKind.Add, ["-"] = ExprKind.Subtract, ["*"] = ExprKind.Multiply, ["/"] = ExprKind.Divide, ["neg"] = ExprKind.Negate,
        ["=="] = ExprKind.Equal, ["!="] = ExprKind.NotEqual, ["<"] = ExprKind.Less, ["<="] = ExprKind.LessOrEqual,
        [">"] = ExprKind.Greater, [">="] = ExprKind.GreaterOrEqual,
        ["and"] = ExprKind.And, ["or"] = ExprKind.Or, ["not"] = ExprKind.Not,
        ["in"] = ExprKind.In, ["between"] = ExprKind.Between, ["isSet"] = ExprKind.IsSet,
        ["if"] = ExprKind.If, ["coalesce"] = ExprKind.Coalesce,
        ["sum"] = ExprKind.Sum, ["count"] = ExprKind.Count, ["any"] = ExprKind.Any, ["all"] = ExprKind.All,
        ["minOf"] = ExprKind.MinOf, ["maxOf"] = ExprKind.MaxOf,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<ExprKind, string> ByKind = Invert();

    private static FrozenDictionary<ExprKind, string> Invert()
    {
        var d = new Dictionary<ExprKind, string>();
        foreach (var (k, v) in ByName) d[v] = k;
        return d.ToFrozenDictionary();
    }

    /// <summary>True when <paramref name="name"/> is an operator key (anything else in an object is a function call).</summary>
    public static bool TryGetOperator(string name, out ExprKind kind) => ByName.TryGetValue(name, out kind);

    /// <summary>The JSON key of an operator (<c>Add → "+"</c>).</summary>
    public static string OperatorName(ExprKind kind) => ByKind[kind];

    /// <summary>True for names functions may not use (operators, <c>var</c>, <c>line</c>).</summary>
    public static bool IsReserved(string name) => ByName.ContainsKey(name) || name is "var" or "line";

    /// <summary>Operators taking ONE operand (not an array).</summary>
    public static bool IsUnary(ExprKind k) => k is ExprKind.Negate or ExprKind.Not or ExprKind.IsSet
        or ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All or ExprKind.MinOf or ExprKind.MaxOf;

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>Parses one expression document. Throws <see cref="JsonException"/> with the JSON path of the problem.</summary>
    public static Expr Read(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        r.Read();
        return Read(ref r, "$");
    }

    /// <summary>Reads the expression at the reader's current token (a host document positioned on a value).</summary>
    public static Expr Read(ref Utf8JsonReader r, string path)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number: return Expr.Lit(Number(ref r));
            case JsonTokenType.String: return Expr.Lit(r.GetString()!);
            case JsonTokenType.True: return Expr.Lit(true);
            case JsonTokenType.False: return Expr.Lit(false);
            case JsonTokenType.Null: return Expr.Null;
            case JsonTokenType.StartObject: break;
            default: throw Error(path, "an expression");
        }
        if (!Next(ref r, out string op)) throw Error(path, "an operator object with one key");
        Expr result;
        string at = path + "." + op;
        if (op == "var") result = Expr.Var(String(ref r, at));
        else if (op == "line") result = Expr.LineVar(String(ref r, at));
        else if (TryGetOperator(op, out var kind))
        {
            if (kind == ExprKind.In) result = ReadIn(ref r, at);
            else if (IsUnary(kind)) result = Expr.Op(kind, Read(ref r, at));
            else result = Expr.Op(kind, ReadArgs(ref r, at));
        }
        else result = Expr.Call(op, ReadArgs(ref r, at));
        if (Next(ref r, out string extra)) throw Error(path, $"one key per expression object (found '{op}' and '{extra}')");
        return result;
    }

    /// <summary>Reads an array of expressions at the reader's current token.</summary>
    public static ImmutableArray<Expr> ReadArgs(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "an array of operands");
        var b = ImmutableArray.CreateBuilder<Expr>();
        int i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) b.Add(Read(ref r, $"{path}[{i++}]"));
        return b.ToImmutable();
    }

    /// <summary>Reads an array of literals (numbers, text, bool, null) at the reader's current token.</summary>
    public static ImmutableArray<RawValue> ReadLiteralList(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "an array of literals");
        var b = ImmutableArray.CreateBuilder<RawValue>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) b.Add(Literal(ref r, path));
        return b.ToImmutable();
    }

    private static Expr ReadIn(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "[value, [list…]]");
        r.Read();
        var value = Read(ref r, path + "[0]");
        r.Read();
        var list = ReadLiteralList(ref r, path + "[1]");
        r.Read();
        if (r.TokenType != JsonTokenType.EndArray) throw Error(path, "exactly two operands");
        return Expr.In(value, list);
    }

    private static RawValue Literal(ref Utf8JsonReader r, string path) => r.TokenType switch
    {
        JsonTokenType.Number => Number(ref r),
        JsonTokenType.String => RawValue.From(r.GetString()),
        JsonTokenType.True => RawValue.From(true),
        JsonTokenType.False => RawValue.From(false),
        JsonTokenType.Null => RawValue.None,
        _ => throw Error(path, "a literal"),
    };

    private static RawValue Number(ref Utf8JsonReader r)
        => r.TryGetInt64(out long l) ? RawValue.From(l) : RawValue.From(r.GetDecimal());

    private static bool Next(ref Utf8JsonReader r, out string name)
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

    private static string String(ref Utf8JsonReader r, string path)
        => r.TokenType == JsonTokenType.String ? r.GetString()! : throw Error(path, "a string");

    private static JsonException Error(string path, string expected) => new($"{path}: expected {expected}.", path, null, null);

    // ── Write (canonical: one shape per node, so stored payloads diff cleanly) ──

    public static void Write(Utf8JsonWriter w, Expr e)
    {
        switch (e.Kind)
        {
            case ExprKind.Literal: WriteLiteral(w, e.Literal); return;
            case ExprKind.Var: w.WriteStartObject(); w.WriteString("var", e.Name); w.WriteEndObject(); return;
            case ExprKind.Line: w.WriteStartObject(); w.WriteString("line", e.Name); w.WriteEndObject(); return;
        }
        w.WriteStartObject();
        if (e.Kind == ExprKind.Call) w.WritePropertyName(e.Name!);
        else w.WritePropertyName(OperatorName(e.Kind));
        if (e.Kind == ExprKind.In)
        {
            w.WriteStartArray();
            Write(w, e.Args[0]);
            w.WriteStartArray();
            foreach (var v in e.List) WriteLiteral(w, v);
            w.WriteEndArray();
            w.WriteEndArray();
        }
        else if (IsUnary(e.Kind)) Write(w, e.Args[0]);
        else
        {
            w.WriteStartArray();
            foreach (var a in e.Args) Write(w, a);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    /// <summary>Writes a literal the way <see cref="ReadLiteralList"/> reads it back (dates as <c>yyyy-MM-dd</c> text).</summary>
    public static void WriteLiteral(Utf8JsonWriter w, in RawValue v)
    {
        switch (v.Kind)
        {
            case ValueKind.None: w.WriteNullValue(); break;
            case ValueKind.Bool: v.TryGet(out bool b); w.WriteBooleanValue(b); break;
            case ValueKind.Int32 or ValueKind.Int64: v.TryGet(out long l); w.WriteNumberValue(l); break;
            case ValueKind.Double: v.TryGet(out double dd); w.WriteNumberValue(dd); break;
            case ValueKind.Decimal: v.TryGet(out decimal m); w.WriteNumberValue(m); break;
            case ValueKind.Date: v.TryGet(out DateOnly date); w.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); break;
            default: v.TryGet(out string? s); w.WriteStringValue(s ?? v.ToString()); break;
        }
    }
}
