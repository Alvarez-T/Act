using System.Buffers;
using System.Globalization;
using System.Text.Json;
using YFex.Localization;
using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// JSON Schema of a situation (Phase 6, AI tools): its fields as a <see cref="TypeShape"/> — enums with their codes, app value
/// types with <c>x-type</c>, the lines as an array of line objects — optionally narrowed by a <see cref="Decision{TSituation}"/>
/// (its limits/options become <c>minimum/maximum/enum</c>). Derived fields are omitted (they are computed, not input).
/// </summary>
public static class SituationJsonSchema
{
    public static string ToJson<TS>(Decision<TS>? decision = null, JsonSchemaOptions? options = null) where TS : ISituation<TS>
        => JsonSchemaWriter.ToJson(ToShape(TS.Schema), decision is null ? null : decision.TryGetConstraints, options);

    /// <summary>The situation schema as a shape (exposed, non-derived fields).</summary>
    public static TypeShape ToShape(SituationSchema schema)
    {
        var members = new List<MemberShape>(schema.Count);
        foreach (var f in schema.Fields)
        {
            if (!f.Exposed || f.Role == FieldRole.Derived) continue;
            if (f.Role == FieldRole.Lines)
            {
                if (f.LineSchema is { } lines) members.Add(new MemberShape(f.Name, JsonKind.Array, element: new MemberShape("", JsonKind.Object, nested: () => ToShape(lines))));
                continue;
            }
            if (f.Kind == ValueKind.EnumCode)
            {
                var options = new EnumMember[f.EnumOptions.Length];
                for (int i = 0; i < options.Length; i++) options[i] = new(f.EnumOptions[i].Code, f.EnumOptions[i].Value);
                members.Add(new MemberShape(f.Name, JsonKind.Enum, enumMembers: options));
            }
            else members.Add(MemberShape.ForRaw(f.Name, f.Type));
        }
        return new TypeShape(schema.Name, [.. members]);
    }
}

/// <summary>
/// Describes a rule set for people and AI (Phase 6): stages → groups (resolution, runtime-rule policy) → rules and tables
/// with their display names and readable conditions ("requires Tier = Gold"). With a <see cref="CompiledRuleSet{TSituation}"/>
/// the runtime rules in force (05 §8) are listed too, with the tier that published them. Pairs with
/// <see cref="Decision{TSituation}.Explain"/> to answer "why didn't this customer get the Gold discount?".
/// </summary>
public static class RuleSetDescription
{
    public static string ToJson<TS>(RuleSetDefinition<TS> set, CompiledRuleSet<TS>? compiled = null, IMessageCatalog? catalog = null,
        CultureInfo? culture = null) where TS : ISituation<TS>
    {
        culture ??= CultureInfo.InvariantCulture;
        var rules = compiled is null ? set.Rules : compiled.Rules;
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("ruleSet", set.Name);
            w.WriteString("situation", TS.Schema.Name);
            if (set.AmountType is { } amount) w.WriteString("amount", amount.Key);
            w.WriteStartArray("stages");
            foreach (var stage in set.Stages)
            {
                w.WriteStartObject();
                w.WriteString("code", stage.Code);
                w.WriteStartArray("groups");
                foreach (var g in set.Groups)
                {
                    if (g.Stage != stage) continue;
                    w.WriteStartObject();
                    w.WriteString("name", g.Name);
                    w.WriteString("resolve", g.Resolve.Kind.ToString());
                    if (g.Dynamic is { } p)
                    {
                        w.WriteStartObject("runtimeRules");
                        w.WriteStartArray("editableAt");
                        foreach (var t in p.EditableAt) w.WriteStringValue(t);
                        w.WriteEndArray();
                        w.WriteString("allow", p.Allow.ToString());
                        if (p.MaxPercent is { } mp && mp.ToRaw().TryGet(out decimal mpv)) w.WriteNumber("maxPercent", mpv);
                        if (p.MaxAmount is { } ma) w.WriteNumber("maxAmount", ma);
                        w.WriteEndObject();
                    }
                    w.WriteStartArray("rules");
                    for (int r = 0; r < rules.Length; r++)
                    {
                        var rule = rules[r];
                        if (rule.Group != g && rule.GroupName != g.Name) continue;
                        bool runtime = r >= set.Rules.Length;
                        w.WriteStartObject();
                        w.WriteString("code", rule.Code);
                        w.WriteString("kind", runtime ? "runtime" : "code");
                        if (catalog is not null) w.WriteString("name", catalog.Format(rule.Code, [RawValue.None], culture));
                        if (rule.IsSealed) w.WriteBoolean("sealed", true);
                        if (runtime && compiled is not null) w.WriteNumber("tier", compiled.RuleTier(r));
                        if (rule.Conditions.Length > 0)
                        {
                            w.WriteStartArray("conditions");
                            foreach (var c in rule.Conditions)
                                w.WriteStringValue(catalog is null ? c.Text
                                    : catalog.Format(c.MessageKey, [c.Value], culture, c.Field is null ? null : TS.Schema.Name + "." + c.Field));
                            w.WriteEndArray();
                        }
                        w.WriteEndObject();
                    }
                    foreach (var t in set.Tables)
                    {
                        if (t.Group != g) continue;
                        w.WriteStartObject();
                        w.WriteString("code", t.Code);
                        w.WriteString("kind", "table");
                        w.WriteString("hitPolicy", t.Schema.HitPolicy.ToString());
                        if (!t.Schema.IsEditable) w.WriteBoolean("sealed", true);
                        w.WriteStartArray("columns");
                        foreach (var col in t.Schema.Columns) w.WriteStringValue($"{col.Role.ToString().ToLowerInvariant()}:{col.Field}");
                        w.WriteEndArray();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
