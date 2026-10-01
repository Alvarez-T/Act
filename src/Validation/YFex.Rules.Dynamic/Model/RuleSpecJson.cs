using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace YFex.Rules.Dynamic;

/// <summary>
/// JSON form of <see cref="RuleSpec"/> (hand-written reader/writer — AOT-safe, no STJ contracts). Expressions are
/// JsonLogic-like: an operator/function name as the single key of an object, its operands as the value.
/// <code>
/// { "code": "tenant.pix-bonus", "group": "PaymentDisc", "priority": 10,
///   "when": { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] },
///   "then": [ { "decrease": { "target": "total", "percent": 3 } } ],
///   "messages": { "en": { "name": "Pix bonus {0}" }, "pt": { "name": "Bônus Pix {0}" } } }
/// </code>
/// </summary>
public static class RuleSpecJson
{
    /// <summary>Operator keys (anything else is a function call).</summary>
    private static readonly FrozenOps Ops = new();

    private sealed class FrozenOps
    {
        public readonly Dictionary<string, ExprKind> ByName = new(StringComparer.Ordinal)
        {
            ["+"] = ExprKind.Add, ["-"] = ExprKind.Subtract, ["*"] = ExprKind.Multiply, ["/"] = ExprKind.Divide, ["neg"] = ExprKind.Negate,
            ["=="] = ExprKind.Equal, ["!="] = ExprKind.NotEqual, ["<"] = ExprKind.Less, ["<="] = ExprKind.LessOrEqual,
            [">"] = ExprKind.Greater, [">="] = ExprKind.GreaterOrEqual,
            ["and"] = ExprKind.And, ["or"] = ExprKind.Or, ["not"] = ExprKind.Not,
            ["in"] = ExprKind.In, ["between"] = ExprKind.Between, ["isSet"] = ExprKind.IsSet,
            ["if"] = ExprKind.If, ["coalesce"] = ExprKind.Coalesce,
            ["sum"] = ExprKind.Sum, ["count"] = ExprKind.Count, ["any"] = ExprKind.Any, ["all"] = ExprKind.All,
            ["minOf"] = ExprKind.MinOf, ["maxOf"] = ExprKind.MaxOf,
        };
        public readonly Dictionary<ExprKind, string> ByKind = new();
        public FrozenOps() { foreach (var (k, v) in ByName) ByKind[v] = k; }
    }

    internal static bool TryGetOperator(string name, out ExprKind kind) => Ops.ByName.TryGetValue(name, out kind);
    internal static string OperatorName(ExprKind kind) => Ops.ByKind[kind];

    /// <summary>Operators taking ONE operand (not an array).</summary>
    internal static bool IsUnary(ExprKind k) => k is ExprKind.Negate or ExprKind.Not or ExprKind.IsSet
        or ExprKind.Sum or ExprKind.Count or ExprKind.Any or ExprKind.All or ExprKind.MinOf or ExprKind.MaxOf;

    // ── Read ────────────────────────────────────────────────────────────────

    /// <summary>Parses a spec. Throws <see cref="JsonException"/> with the JSON path of the problem.</summary>
    public static RuleSpec Read(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        r.Read();
        Expect(ref r, JsonTokenType.StartObject, "$");
        string? code = null, group = null, description = null;
        int priority = 0;
        Expr? when = null;
        var then = ImmutableArray<DirectiveSpec>.Empty;
        var messages = ImmutableDictionary<string, ImmutableDictionary<string, string>>.Empty;
        while (Next(ref r, out string name))
        {
            switch (name)
            {
                case "code": code = String(ref r, "$.code"); break;
                case "group": group = String(ref r, "$.group"); break;
                case "description": description = String(ref r, "$.description"); break;
                case "priority": priority = r.TokenType == JsonTokenType.Number ? r.GetInt32() : throw Error("$.priority", "a number"); break;
                case "when": when = r.TokenType == JsonTokenType.Null ? null : ReadExpr(ref r, "$.when"); break;
                case "then": then = ReadDirectives(ref r); break;
                case "messages": messages = ReadMessages(ref r); break;
                default: r.Skip(); break;
            }
        }
        if (string.IsNullOrEmpty(code)) throw Error("$.code", "a rule code");
        if (string.IsNullOrEmpty(group)) throw Error("$.group", "a group name");
        return new RuleSpec(code, group, priority, when, then, messages, description);
    }

    /// <summary>Parses one expression (editor / AI helpers).</summary>
    public static Expr ReadExpression(ReadOnlySpan<byte> json)
    {
        var r = new Utf8JsonReader(json);
        r.Read();
        return ReadExpr(ref r, "$");
    }

    private static Expr ReadExpr(ref Utf8JsonReader r, string path)
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
            else if (IsUnary(kind)) result = Expr.Op(kind, ReadExpr(ref r, at));
            else result = Expr.Op(kind, ReadArgs(ref r, at));
        }
        else result = Expr.Call(op, ReadArgs(ref r, at));
        if (Next(ref r, out string extra)) throw Error(path, $"one key per expression object (found '{op}' and '{extra}')");
        return result;
    }

    private static ImmutableArray<Expr> ReadArgs(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "an array of operands");
        var b = ImmutableArray.CreateBuilder<Expr>();
        int i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) b.Add(ReadExpr(ref r, $"{path}[{i++}]"));
        return b.ToImmutable();
    }

    private static Expr ReadIn(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "[value, [list…]]");
        r.Read();
        var value = ReadExpr(ref r, path + "[0]");
        r.Read();
        var list = ReadLiteralList(ref r, path + "[1]");
        r.Read();
        if (r.TokenType != JsonTokenType.EndArray) throw Error(path, "exactly two operands");
        return Expr.In(value, list);
    }

    private static ImmutableArray<RawValue> ReadLiteralList(ref Utf8JsonReader r, string path)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error(path, "an array of literals");
        var b = ImmutableArray.CreateBuilder<RawValue>();
        while (r.Read() && r.TokenType != JsonTokenType.EndArray) b.Add(Literal(ref r, path));
        return b.ToImmutable();
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

    private static ImmutableArray<DirectiveSpec> ReadDirectives(ref Utf8JsonReader r)
    {
        if (r.TokenType != JsonTokenType.StartArray) throw Error("$.then", "an array of directives");
        var b = ImmutableArray.CreateBuilder<DirectiveSpec>();
        int i = 0;
        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
        {
            string path = $"$.then[{i++}]";
            Expect(ref r, JsonTokenType.StartObject, path);
            if (!Next(ref r, out string kindName)) throw Error(path, "{\"deny|warn|limit|grant|decrease|increase|approval\": {…}}");
            var kind = kindName switch
            {
                "deny" => DirectiveSpecKind.Deny, "warn" => DirectiveSpecKind.Warn, "limit" => DirectiveSpecKind.Limit,
                "grant" => DirectiveSpecKind.Grant, "decrease" => DirectiveSpecKind.Decrease, "increase" => DirectiveSpecKind.Increase,
                "approval" => DirectiveSpecKind.Approval,
                _ => throw Error(path, $"a directive kind (not '{kindName}')"),
            };
            path += "." + kindName;
            Expect(ref r, JsonTokenType.StartObject, path);
            var d = new DirectiveSpec(kind);
            while (Next(ref r, out string p))
            {
                string at = path + "." + p;
                d = p switch
                {
                    "field" => d with { Field = String(ref r, at) },
                    "path" => d with { Path = String(ref r, at) },
                    "code" => d with { Code = String(ref r, at) },
                    "permission" => d with { Permission = String(ref r, at) },
                    "args" => d with { Args = ReadArgs(ref r, at) },
                    "min" => d with { Min = ReadExpr(ref r, at) },
                    "max" => d with { Max = ReadExpr(ref r, at) },
                    "percent" => d with { Percent = ReadExpr(ref r, at) },
                    "amount" => d with { Amount = ReadExpr(ref r, at) },
                    "options" => d with { Options = ReadLiteralList(ref r, at) },
                    "target" => d with
                    {
                        LineTarget = String(ref r, at) switch
                        {
                            "line" => true, "total" => false, var t => throw Error(at, $"\"total\" or \"line\" (not '{t}')"),
                        },
                    },
                    "exceed" => d with
                    {
                        Exceed = String(ref r, at) switch
                        {
                            "deny" => Exceed.Deny, "warn" => Exceed.Warn, "approval" => Exceed.RequireApproval,
                            var x => throw Error(at, $"deny | warn | approval (not '{x}')"),
                        },
                    },
                    _ => Skip(ref r, d),
                };
            }
            if (Next(ref r, out string extra)) throw Error(path, $"one directive per object (found '{extra}')");
            b.Add(d);
        }
        return b.ToImmutable();

        static DirectiveSpec Skip(ref Utf8JsonReader r, DirectiveSpec d) { r.Skip(); return d; }
    }

    private static ImmutableDictionary<string, ImmutableDictionary<string, string>> ReadMessages(ref Utf8JsonReader r)
    {
        Expect(ref r, JsonTokenType.StartObject, "$.messages");
        var b = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
        while (Next(ref r, out string culture))
        {
            Expect(ref r, JsonTokenType.StartObject, "$.messages." + culture);
            var m = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            while (Next(ref r, out string key)) m[key] = String(ref r, $"$.messages.{culture}.{key}");
            b[culture] = m.ToImmutable();
        }
        return b.ToImmutable();
    }

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

    private static void Expect(ref Utf8JsonReader r, JsonTokenType type, string path)
    {
        if (r.TokenType != type) throw Error(path, type == JsonTokenType.StartObject ? "an object" : type.ToString());
    }

    private static JsonException Error(string path, string expected) => new($"{path}: expected {expected}.", path, null, null);

    // ── Write (canonical: fixed key order, so stored payloads diff cleanly) ──

    public static byte[] Write(RuleSpec spec)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("code", spec.Code);
            w.WriteString("group", spec.Group);
            if (spec.Priority != 0) w.WriteNumber("priority", spec.Priority);
            if (spec.Description is { } d) w.WriteString("description", d);
            if (spec.When is { } when) { w.WritePropertyName("when"); WriteExpr(w, when); }
            w.WriteStartArray("then");
            foreach (var x in spec.Then) WriteDirective(w, x);
            w.WriteEndArray();
            if (!spec.Messages.IsEmpty)
            {
                w.WriteStartObject("messages");
                foreach (var culture in Sorted(spec.Messages.Keys))
                {
                    w.WriteStartObject(culture);
                    var m = spec.Messages[culture];
                    foreach (var key in Sorted(m.Keys)) w.WriteString(key, m[key]);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static void WriteExpr(Utf8JsonWriter w, Expr e)
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
            WriteExpr(w, e.Args[0]);
            w.WriteStartArray();
            foreach (var v in e.List) WriteLiteral(w, v);
            w.WriteEndArray();
            w.WriteEndArray();
        }
        else if (IsUnary(e.Kind)) WriteExpr(w, e.Args[0]);
        else
        {
            w.WriteStartArray();
            foreach (var a in e.Args) WriteExpr(w, a);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void WriteDirective(Utf8JsonWriter w, DirectiveSpec d)
    {
        w.WriteStartObject();
        w.WriteStartObject(d.Kind switch
        {
            DirectiveSpecKind.Deny => "deny", DirectiveSpecKind.Warn => "warn", DirectiveSpecKind.Limit => "limit",
            DirectiveSpecKind.Grant => "grant", DirectiveSpecKind.Decrease => "decrease", DirectiveSpecKind.Increase => "increase",
            _ => "approval",
        });
        if (d.Field is { } f) w.WriteString("field", f);
        if (d.Path is { } p) w.WriteString("path", p);
        if (d.Code is { } c) w.WriteString("code", c);
        if (d.Permission is { } perm) w.WriteString("permission", perm);
        if (!d.Args.IsDefaultOrEmpty)
        {
            w.WriteStartArray("args");
            foreach (var a in d.Args) WriteExpr(w, a);
            w.WriteEndArray();
        }
        if (d.Kind is DirectiveSpecKind.Decrease or DirectiveSpecKind.Increase) w.WriteString("target", d.LineTarget ? "line" : "total");
        if (d.Percent is { } pc) { w.WritePropertyName("percent"); WriteExpr(w, pc); }
        if (d.Amount is { } am) { w.WritePropertyName("amount"); WriteExpr(w, am); }
        if (d.Min is { } min) { w.WritePropertyName("min"); WriteExpr(w, min); }
        if (d.Max is { } max) { w.WritePropertyName("max"); WriteExpr(w, max); }
        if (d.Kind == DirectiveSpecKind.Limit && d.Exceed != Exceed.Deny)
            w.WriteString("exceed", d.Exceed == Exceed.Warn ? "warn" : "approval");
        if (!d.Options.IsDefaultOrEmpty)
        {
            w.WriteStartArray("options");
            foreach (var v in d.Options) WriteLiteral(w, v);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteLiteral(Utf8JsonWriter w, in RawValue v)
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

    private static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
