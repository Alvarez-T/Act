using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace YFex.Rules.Dynamic;

/// <summary>
/// JSON form of <see cref="RuleSpec"/> (hand-written reader/writer — AOT-safe, no STJ contracts). The envelope (code, group,
/// directives, messages) is read here; every expression (<c>when</c>, directive values, message args) goes through
/// <see cref="ExprJson"/> (YFex.Expressions), whose format is JsonLogic-like:
/// <code>
/// { "code": "tenant.pix-bonus", "group": "PaymentDisc", "priority": 10,
///   "when": { "and": [ { "==": [ { "var": "Payment" }, "Pix" ] }, { ">=": [ { "var": "Total" }, 500 ] } ] },
///   "then": [ { "decrease": { "target": "total", "percent": 3 } } ],
///   "messages": { "en": { "name": "Pix bonus {0}" }, "pt": { "name": "Bônus Pix {0}" } } }
/// </code>
/// </summary>
public static class RuleSpecJson
{
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
                case "when": when = r.TokenType == JsonTokenType.Null ? null : ExprJson.Read(ref r, "$.when"); break;
                case "then": then = ReadDirectives(ref r); break;
                case "messages": messages = ReadMessages(ref r); break;
                default: r.Skip(); break;
            }
        }
        if (string.IsNullOrEmpty(code)) throw Error("$.code", "a rule code");
        if (string.IsNullOrEmpty(group)) throw Error("$.group", "a group name");
        return new RuleSpec(code, group, priority, when, then, messages, description);
    }

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
                    "args" => d with { Args = ExprJson.ReadArgs(ref r, at) },
                    "min" => d with { Min = ExprJson.Read(ref r, at) },
                    "max" => d with { Max = ExprJson.Read(ref r, at) },
                    "percent" => d with { Percent = ExprJson.Read(ref r, at) },
                    "amount" => d with { Amount = ExprJson.Read(ref r, at) },
                    "options" => d with { Options = ExprJson.ReadLiteralList(ref r, at) },
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
            if (spec.When is { } when) { w.WritePropertyName("when"); ExprJson.Write(w, when); }
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
            foreach (var a in d.Args) ExprJson.Write(w, a);
            w.WriteEndArray();
        }
        if (d.Kind is DirectiveSpecKind.Decrease or DirectiveSpecKind.Increase) w.WriteString("target", d.LineTarget ? "line" : "total");
        if (d.Percent is { } pc) { w.WritePropertyName("percent"); ExprJson.Write(w, pc); }
        if (d.Amount is { } am) { w.WritePropertyName("amount"); ExprJson.Write(w, am); }
        if (d.Min is { } min) { w.WritePropertyName("min"); ExprJson.Write(w, min); }
        if (d.Max is { } max) { w.WritePropertyName("max"); ExprJson.Write(w, max); }
        if (d.Kind == DirectiveSpecKind.Limit && d.Exceed != Exceed.Deny)
            w.WriteString("exceed", d.Exceed == Exceed.Warn ? "warn" : "approval");
        if (!d.Options.IsDefaultOrEmpty)
        {
            w.WriteStartArray("options");
            foreach (var v in d.Options) ExprJson.WriteLiteral(w, v);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static List<string> Sorted(IEnumerable<string> keys)
    {
        var list = new List<string>(keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
