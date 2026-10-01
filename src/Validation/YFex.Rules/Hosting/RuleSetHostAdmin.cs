using YFex.Validation;

namespace YFex.Rules;

/// <summary>
/// Non-generic view of a <see cref="RuleSetHost{TSituation}"/> for the publish pipeline (05 §4): the admin module handles
/// many rule sets without knowing their situation types. Validation here is pure (no I/O, no authorization).
/// </summary>
public partial interface IRuleSetHost
{
    string Name { get; }
    RuleCatalog Catalog { get; }

    /// <summary>Schema of the situation (its <see cref="SituationSchema.Tiers"/> name the tiers data can be published at).</summary>
    SituationSchema Situation { get; }

    /// <summary>Schema of a table (columns, editability, publish permission) — the editor grid is built from it.</summary>
    bool TryGetTable(string code, out TableSchema schema);

    /// <summary>Parameters of a code rule (editable numbers) and whether the rule is sealed.</summary>
    bool TryGetRule(string code, out bool isSealed, out RuleParameter[] parameters);

    /// <summary>Publish-time checks of a table version: editable at the path's tier, parses, rows + guardrails + overlaps.</summary>
    ValidationResult ValidateTable(string code, in RuleTierPath tierPath, ReadOnlySpan<byte> rowsJson);

    /// <summary>Publish-time checks of parameter values: known, publishable, editable at the tier, inside the sealed guard.</summary>
    ValidationResult ValidateParameters(string ruleCode, in RuleTierPath tierPath, ReadOnlySpan<byte> valuesJson);

    /// <summary>Override checks: the target exists, is not sealed, the replacement exists.</summary>
    ValidationResult ValidateOverride(string code, in RuleTierPath tierPath, in RuleOverrideAction action);

    /// <summary>Runtime rule checks (05 §8) through the host's codec; no codec → <c>rules.publish.no-runtime-rules</c>.</summary>
    ValidationResult ValidateRule(string code, in RuleTierPath tierPath, ReadOnlySpan<byte> payload);

    /// <summary>The rule set accepts runtime rules (a codec is set).</summary>
    bool AcceptsRuntimeRules { get; }

    /// <summary>Row document of a table's code defaults (editor "reset to defaults", exports).</summary>
    byte[] DefaultRowsJson(string tableCode);

    /// <summary>
    /// The form a VALID payload is stored in: parsed and re-written by the codec, so every table row carries a stable id
    /// (rows sent without one get a new id ONCE, here — not on every decode) and formatting/key order never show up in
    /// diffs. Call after the matching <c>Validate…</c> succeeded.
    /// </summary>
    byte[] Canonicalize(RuleDataKind kind, string code, ReadOnlySpan<byte> payload);

    /// <summary>Decodes and publishes a stored record.</summary>
    void Apply(RuleDataRecord record);

    ValueTask<IReadOnlyList<(RuleDataRecord Record, Exception Error)>> LoadAsync(IRuleStore store, CancellationToken ct = default);
}

/// <summary>Violation codes of the publish pipeline.</summary>
public static class RulePublishCodes
{
    public const string UnknownTable = "rules.publish.unknown-table";
    public const string UnknownRule = "rules.publish.unknown-rule";
    public const string UnknownParameter = "rules.publish.unknown-parameter";
    public const string NotEditable = "rules.publish.not-editable";
    public const string NotPublishable = "rules.publish.no-codec";
    public const string Guard = "rules.publish.guard";
    public const string Malformed = "rules.publish.malformed";
    public const string NoRuntimeRules = "rules.publish.no-runtime-rules";
    public const string CodeTaken = "rules.publish.code-taken";
    public const string Sealed = RuleEngineCodes.Sealed;
}

public sealed partial class RuleSetHost<TSituation> : IRuleSetHost
{
    string IRuleSetHost.Name => Definition.Name;
    RuleCatalog IRuleSetHost.Catalog => Definition.Catalog;
    SituationSchema IRuleSetHost.Situation => TSituation.Schema;

    public bool TryGetTable(string code, out TableSchema schema)
    {
        if (Definition.TryGetTable(code, out var t)) { schema = t.Schema; return true; }
        schema = null!;
        return false;
    }

    public bool TryGetRule(string code, out bool isSealed, out RuleParameter[] parameters)
    {
        if (Definition.TryGetRule(code, out var r)) { isSealed = r.IsSealed; parameters = r.Parameters.ToArray(); return true; }
        isSealed = false;
        parameters = [];
        return false;
    }

    public ValidationResult ValidateTable(string code, in RuleTierPath tierPath, ReadOnlySpan<byte> rowsJson)
    {
        using var s = ViolationWriter.Begin(out var w);
        if (!Definition.TryGetTable(code, out var table))
        {
            w.Add(FieldPath.Of("TableCode"), RulePublishCodes.UnknownTable, RawValue.From(code));
            return w.ToResult();
        }
        if (!EditableAt(table.Schema.EditableAt, tierPath.Depth))
        {
            w.Add(FieldPath.Of("TierPath"), RulePublishCodes.NotEditable, RawValue.From(TierName(tierPath.Depth)));
            return w.ToResult();
        }
        TableVersion version;
        try { version = table.Parse(rowsJson, new TableVersionInfo(code, 0, tierPath, DateOnly.MinValue)); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            w.Add(new Violation(FieldPath.Of("RowsJson"), RulePublishCodes.Malformed, detail: e.Message));
            return w.ToResult();
        }
        table.Check(version, ref w);
        return w.ToResult();
    }

    public ValidationResult ValidateParameters(string ruleCode, in RuleTierPath tierPath, ReadOnlySpan<byte> valuesJson)
    {
        using var s = ViolationWriter.Begin(out var w);
        if (!Definition.TryGetRule(ruleCode, out var rule))
        {
            w.Add(FieldPath.Of("RuleCode"), RulePublishCodes.UnknownRule, RawValue.From(ruleCode));
            return w.ToResult();
        }
        var parameters = rule.Parameters;
        System.Collections.Immutable.ImmutableDictionary<string, object?> values;
        try { values = RuleDataCodec.ReadParameters(parameters, valuesJson); }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or NotSupportedException or InvalidOperationException)
        {
            w.Add(new Violation(FieldPath.Of("ValuesJson"), RulePublishCodes.Malformed, detail: e.Message));
            return w.ToResult();
        }
        foreach (var p in parameters)
        {
            if (!values.TryGetValue(p.Name, out var value)) continue;
            var path = FieldPath.Of("Values." + p.Name);
            if (!p.CanPublish) w.Add(path, RulePublishCodes.NotPublishable);
            else if (!EditableAt(p.EditableAt, tierPath.Depth)) w.Add(path, RulePublishCodes.NotEditable, RawValue.From(TierName(tierPath.Depth)));
            else if (!p.IsWithinGuard(value)) w.Add(path, RulePublishCodes.Guard);
        }
        return w.ToResult();
    }

    public ValidationResult ValidateOverride(string code, in RuleTierPath tierPath, in RuleOverrideAction action)
    {
        using var s = ViolationWriter.Begin(out var w);
        bool isRule = Definition.TryGetRule(code, out var rule);
        bool isTable = Definition.TryGetTable(code, out var table);
        if (!isRule && !isTable && !IsRuntimeRule(code)) w.Add(FieldPath.Of("RuleCode"), RulePublishCodes.UnknownRule, RawValue.From(code));
        else if (tierPath.IsCode || tierPath.Depth > TSituation.Schema.Tiers.Length)
            w.Add(FieldPath.Of("TierPath"), RulePublishCodes.NotEditable, RawValue.From(TierName(tierPath.Depth)));
        else if ((isRule && rule.IsSealed) || (isTable && !table.Schema.IsEditable && action.Kind != RuleOverrideKind.Enable))
            w.Add(FieldPath.Of("RuleCode"), RulePublishCodes.Sealed, RawValue.From(code));
        else if (action.Kind == RuleOverrideKind.ReplaceWith && action.ReplacementCode is { } r
                 && !Definition.TryGetRule(r, out _) && !Definition.TryGetTable(r, out _))
            w.Add(FieldPath.Of("ReplacementCode"), RulePublishCodes.UnknownRule, RawValue.From(r));
        return w.ToResult();
    }

    public bool AcceptsRuntimeRules => RuntimeRules is not null;

    public ValidationResult ValidateRule(string code, in RuleTierPath tierPath, ReadOnlySpan<byte> payload)
    {
        using var s = ViolationWriter.Begin(out var w);
        if (RuntimeRules is null) w.Add(FieldPath.Of("RuleCode"), RulePublishCodes.NoRuntimeRules, RawValue.From(Definition.Name));
        else if (Definition.TryGetRule(code, out _) || Definition.TryGetTable(code, out _)) w.Add(FieldPath.Of("RuleCode"), RulePublishCodes.CodeTaken, RawValue.From(code));
        else if (tierPath.IsCode) w.Add(FieldPath.Of("TierPath"), RulePublishCodes.NotEditable, RawValue.From(TierName(0)));
        if (w.HasErrors) return w.ToResult();
        return RuntimeRules!.Validate(Definition, code, tierPath, payload);
    }

    public byte[] DefaultRowsJson(string tableCode)
        => Definition.TryGetTable(tableCode, out var t) ? t.SerializeDefaults() : throw new ArgumentException($"Unknown table '{tableCode}'.", nameof(tableCode));

    public byte[] Canonicalize(RuleDataKind kind, string code, ReadOnlySpan<byte> payload)
    {
        switch (kind)
        {
            case RuleDataKind.Table:
                if (!Definition.TryGetTable(code, out var table)) throw new ArgumentException($"Unknown table '{code}'.", nameof(code));
                return table.Serialize(table.Parse(payload, new TableVersionInfo(code, 0, RuleTierPath.Code, DateOnly.MinValue)));
            case RuleDataKind.Parameters:
                if (!Definition.TryGetRule(code, out var rule)) throw new ArgumentException($"Unknown rule '{code}'.", nameof(code));
                return RuleDataCodec.WriteParameters(rule.Parameters, RuleDataCodec.ReadParameters(rule.Parameters, payload));
            case RuleDataKind.Override:
                return RuleDataCodec.WriteOverride(RuleDataCodec.ReadOverride(payload));
            case RuleDataKind.Rule:
                return (RuntimeRules ?? throw new NotSupportedException("No runtime rule codec.")).Canonicalize(Definition, payload);
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    /// <summary>Runtime publishing never targets the code tier (that ships with the build).</summary>
    private static bool EditableAt(ReadOnlySpan<string> editableAt, int tier) => tier > 0 && TSituation.Schema.AllowsTier(editableAt, tier);

    private static string TierName(int tier) => TSituation.Schema.TierName(tier);
}
