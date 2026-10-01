using System.Buffers;
using System.Text.Json;

namespace YFex.Rules.Dynamic;

/// <summary>
/// What a rule editor or an AI needs to author rules for one rule set (Phase 6, composer-design §12): the
/// <see cref="CatalogJson{TS}"/> lists fields, operators, functions and the groups that accept runtime rules (with their
/// caps); <see cref="SpecJsonSchema{TS}"/> is the JSON Schema of a <see cref="RuleSpec"/> specialized to this rule set (only its
/// fields, implemented functions and open groups) — usable as an LLM structured-output schema. The loop is: generate →
/// <see cref="DynamicRules{TS}.Check"/> (violations with JSON paths back to the model) → what-if simulate → a person publishes.
/// </summary>
public static class DynamicRulesAuthoring
{
    private static readonly (string Op, string Arity, string Description)[] Operators =
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

    // ── Catalog ─────────────────────────────────────────────────────────────

    public static string CatalogJson<TS>(this DynamicRules<TS> rules, RuleSetDefinition<TS> set) where TS : ISituation<TS>
        => Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("ruleSet", set.Name);
            w.WriteString("situation", TS.Schema.Name);
            if (set.AmountType is { } amount) w.WriteString("amount", amount.Key);
            w.WriteStartArray("fields");
            WriteFields(w, TS.Schema);
            w.WriteEndArray();
            if (TS.Schema.LineSchema is { } lines)
            {
                w.WriteStartArray("lineFields");
                WriteFields(w, lines);
                w.WriteEndArray();
            }
            w.WriteStartArray("specials");
            if (set.AmountType is { } a) Special(w, "$base", a.Key, "the running amount at the rule's stage");
            Special(w, "$effectiveDate", "date", "the business date the situation is evaluated for (never the clock)");
            if (TS.Schema.LineSchema is not null) Special(w, "$lineIndex", "int64", "index of the current line (per-line rules)");
            w.WriteEndArray();
            w.WriteStartArray("operators");
            foreach (var (op, arity, description) in Operators)
            {
                w.WriteStartObject();
                w.WriteString("op", op);
                w.WriteString("arity", arity);
                w.WriteString("description", description);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("functions");
            foreach (var f in Sorted(rules.Functions))
            {
                w.WriteStartObject();
                w.WriteString("name", f.Name);
                w.WriteString("signature", f.Signature);
                w.WriteString("description", f.Description);
                if (f.Category is { } c) w.WriteString("category", c);
                if (f.IsContract) w.WriteBoolean("implemented", false);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("groups");
            foreach (var g in set.Groups)
            {
                if (g.Dynamic is not { } p) continue;
                w.WriteStartObject();
                w.WriteString("name", g.Name);
                w.WriteString("stage", g.Stage.Code);
                w.WriteString("resolve", g.Resolve.Kind.ToString());
                w.WriteStartArray("editableAt");
                foreach (var t in p.EditableAt) w.WriteStringValue(t);
                w.WriteEndArray();
                w.WriteStartArray("directives");
                foreach (var d in Families(p.Allow)) w.WriteStringValue(d);
                w.WriteEndArray();
                if (p.MaxPercent is { } mp && mp.ToRaw().TryGet(out decimal mpv)) w.WriteNumber("maxPercent", mpv);
                if (p.MaxAmount is { } ma) w.WriteNumber("maxAmount", ma);
                w.WriteNumber("maxRules", p.MaxRules);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });

    private static void WriteFields(Utf8JsonWriter w, SituationSchema schema)
    {
        foreach (var f in schema.Fields)
        {
            if (!f.Exposed || f.Role == FieldRole.Lines) continue;
            w.WriteStartObject();
            w.WriteString("name", f.Name);
            w.WriteString("type", f.Kind == ValueKind.EnumCode ? "enum" : f.Type.Key);
            if (f.Role != FieldRole.Normal) w.WriteString("role", f.Role.ToString().ToLowerInvariant());
            if (f.Kind == ValueKind.EnumCode)
            {
                w.WriteStartArray("options");
                foreach (var o in f.EnumOptions) w.WriteStringValue(o.Code);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
    }

    private static void Special(Utf8JsonWriter w, string name, string type, string description)
    {
        w.WriteStartObject();
        w.WriteString("name", name);
        w.WriteString("type", type);
        w.WriteString("description", description);
        w.WriteEndObject();
    }

    // ── JSON Schema of a spec ───────────────────────────────────────────────

    public static string SpecJsonSchema<TS>(this DynamicRules<TS> rules, RuleSetDefinition<TS> set) where TS : ISituation<TS>
        => Write(w =>
        {
            var allow = DynamicDirectives.None;
            foreach (var g in set.Groups) if (g.Dynamic is { } p) allow |= p.Allow;
            var schema = TS.Schema;

            w.WriteStartObject();
            w.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            w.WriteString("title", $"Runtime rule for rule set '{set.Name}'");
            w.WriteString("type", "object");
            w.WriteStartArray("required"); w.WriteStringValue("code"); w.WriteStringValue("group"); w.WriteStringValue("then"); w.WriteEndArray();
            w.WriteStartObject("properties");
            w.WriteStartObject("code"); w.WriteString("type", "string"); w.WriteString("pattern", "^[A-Za-z0-9]+([.\\-_][A-Za-z0-9]+)*$"); w.WriteEndObject();
            w.WriteStartObject("group");
            w.WriteStartArray("enum");
            foreach (var g in set.Groups) if (g.Dynamic is not null) w.WriteStringValue(g.Name);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("priority"); w.WriteString("type", "integer"); w.WriteEndObject();
            w.WriteStartObject("description"); w.WriteString("type", "string"); w.WriteEndObject();
            w.WriteStartObject("when"); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject();
            w.WriteStartObject("then");
            w.WriteString("type", "array");
            w.WriteNumber("minItems", 1);
            w.WriteNumber("maxItems", Binder<TS>.MaxDirectives);
            w.WriteStartObject("items"); w.WriteString("$ref", "#/$defs/directive"); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("messages");
            w.WriteString("description", "culture (\"\" = invariant, \"en\", \"pt\"…) → key → template; key \"name\" is the rule's display name");
            w.WriteString("type", "object");
            w.WriteStartObject("additionalProperties");
            w.WriteString("type", "object");
            w.WriteStartObject("additionalProperties"); w.WriteString("type", "string"); w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();                                                      // properties
            w.WriteBoolean("additionalProperties", false);

            w.WriteStartObject("$defs");
            // expr
            w.WriteStartObject("expr");
            w.WriteStartArray("anyOf");
            w.WriteStartObject(); w.WriteStartArray("type"); foreach (var t in new[] { "number", "string", "boolean", "null" }) w.WriteStringValue(t); w.WriteEndArray(); w.WriteEndObject();
            Ref(w, "var"); if (schema.LineSchema is not null) Ref(w, "line"); Ref(w, "op"); Ref(w, "call");
            w.WriteEndArray();
            w.WriteEndObject();
            // var
            var vars = new List<string>();
            foreach (var f in schema.Fields) if (f.Exposed && f.Role != FieldRole.Lines) vars.Add(f.Name);
            if (set.AmountType is not null) vars.Add("$base");
            vars.Add("$effectiveDate");
            if (schema.LineSchema is not null) vars.Add("$lineIndex");
            SingleKey(w, "var", vars);
            if (schema.LineSchema is { } lines)
            {
                var lineNames = new List<string>();
                foreach (var f in lines.Fields) if (f.Exposed) lineNames.Add(f.Name);
                SingleKey(w, "line", lineNames);
            }
            // op
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
            // call (implemented functions only)
            w.WriteStartObject("call");
            w.WriteString("type", "object"); w.WriteNumber("minProperties", 1); w.WriteNumber("maxProperties", 1);
            w.WriteStartObject("properties");
            foreach (var f in Sorted(rules.Functions))
            {
                if (f.IsContract) continue;
                w.WriteStartObject(f.Name);
                w.WriteString("description", $"{f.Signature} — {f.Description}");
                ExprArray(w, f.Parameters.Length);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteBoolean("additionalProperties", false);
            w.WriteEndObject();
            // directives
            WriteDirectives(w, allow, schema, set.AmountType is not null);
            w.WriteEndObject();                                                      // $defs
            w.WriteEndObject();
        });

    private static void WriteDirectives(Utf8JsonWriter w, DynamicDirectives allow, SituationSchema schema, bool hasAmount)
    {
        var fields = new List<string>();
        foreach (var f in schema.Fields) if (f.Exposed && f.Role != FieldRole.Lines) fields.Add(f.Name);
        w.WriteStartObject("directive");
        w.WriteStartArray("oneOf");
        if ((allow & DynamicDirectives.Deny) != 0) Message(w, "deny");
        if ((allow & DynamicDirectives.Warn) != 0) Message(w, "warn");
        if ((allow & DynamicDirectives.Limit) != 0)
            Directive(w, "limit", ["field"], () =>
            {
                Enum(w, "field", fields);
                ExprProp(w, "min"); ExprProp(w, "max");
                Enum(w, "exceed", ["deny", "warn", "approval"]);
                Str(w, "permission");
            });
        if ((allow & DynamicDirectives.Grant) != 0)
            Directive(w, "grant", ["field", "options"], () =>
            {
                Enum(w, "field", fields);
                w.WriteStartObject("options"); w.WriteString("type", "array"); w.WriteNumber("minItems", 1); w.WriteEndObject();
            });
        if (hasAmount)
            foreach (var (name, flag) in new[] { ("decrease", DynamicDirectives.Decrease), ("increase", DynamicDirectives.Increase) })
                if ((allow & flag) != 0)
                    Directive(w, name, ["target"], () =>
                    {
                        Enum(w, "target", schema.LineSchema is null ? ["total"] : ["total", "line"]);
                        ExprProp(w, "percent"); ExprProp(w, "amount"); ExprProp(w, "max");
                    }, oneOfRequired: ["percent", "amount"]);
        if ((allow & DynamicDirectives.RequireApproval) != 0)
            Directive(w, "approval", ["permission"], () => { Str(w, "permission"); Str(w, "path"); Str(w, "code"); });
        w.WriteEndArray();
        w.WriteEndObject();

        void Message(Utf8JsonWriter w, string kind) => Directive(w, kind, [], () =>
        {
            Str(w, "path"); Str(w, "code");
            w.WriteStartObject("args"); ExprArray(w, maxItems: 2, minItems: 0); w.WriteEndObject();
        });
    }

    private static void Directive(Utf8JsonWriter w, string kind, string[] required, Action body, string[]? oneOfRequired = null)
    {
        w.WriteStartObject();
        w.WriteString("type", "object");
        w.WriteStartArray("required"); w.WriteStringValue(kind); w.WriteEndArray();
        w.WriteStartObject("properties");
        w.WriteStartObject(kind);
        w.WriteString("type", "object");
        if (required.Length > 0) { w.WriteStartArray("required"); foreach (var r in required) w.WriteStringValue(r); w.WriteEndArray(); }
        if (oneOfRequired is not null)
        {
            w.WriteStartArray("oneOf");
            foreach (var r in oneOfRequired) { w.WriteStartObject(); w.WriteStartArray("required"); w.WriteStringValue(r); w.WriteEndArray(); w.WriteEndObject(); }
            w.WriteEndArray();
        }
        w.WriteStartObject("properties");
        body();
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
        w.WriteEndObject();
        w.WriteBoolean("additionalProperties", false);
        w.WriteEndObject();
    }

    private static void SingleKey(Utf8JsonWriter w, string key, List<string> names)
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

    private static void ExprArray(Utf8JsonWriter w, int? exactly = null, int? maxItems = null, int? minItems = null)
    {
        w.WriteString("type", "array");
        w.WriteStartObject("items"); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject();
        if (exactly is { } n) { w.WriteNumber("minItems", n); w.WriteNumber("maxItems", n); return; }
        if (minItems is { } min) w.WriteNumber("minItems", min);
        if (maxItems is { } max) w.WriteNumber("maxItems", max);
    }

    private static void ExprProp(Utf8JsonWriter w, string name) { w.WriteStartObject(name); w.WriteString("$ref", "#/$defs/expr"); w.WriteEndObject(); }
    private static void Str(Utf8JsonWriter w, string name) { w.WriteStartObject(name); w.WriteString("type", "string"); w.WriteEndObject(); }
    private static void Ref(Utf8JsonWriter w, string def) { w.WriteStartObject(); w.WriteString("$ref", "#/$defs/" + def); w.WriteEndObject(); }

    private static void Enum(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartObject(name);
        w.WriteStartArray("enum");
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static List<string> Families(DynamicDirectives allow)
    {
        var list = new List<string>();
        foreach (var (name, flag) in new[] { ("deny", DynamicDirectives.Deny), ("warn", DynamicDirectives.Warn), ("limit", DynamicDirectives.Limit),
            ("grant", DynamicDirectives.Grant), ("decrease", DynamicDirectives.Decrease), ("increase", DynamicDirectives.Increase),
            ("approval", DynamicDirectives.RequireApproval) })
            if ((allow & flag) != 0) list.Add(name);
        return list;
    }

    private static List<RuleFunction> Sorted(FunctionCatalog catalog)
    {
        var list = new List<RuleFunction>(catalog.All);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer)) body(w);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
