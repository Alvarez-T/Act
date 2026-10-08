using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using YFex.Helpers;
using YFex.Localization;
using YFex.Validation;

namespace YFex.Rules.Dynamic;

/// <summary>
/// Runtime-authored rules for one situation type (05 §8) — the <see cref="IRuntimeRuleCodec{TSituation}"/> a host plugs in:
/// <code>
/// var dynamic = new DynamicRules&lt;SaleSituation&gt;(FunctionCatalog.BuiltIn.With(CalendarFunctions.All));
/// var host = SaleRules.CreateHost().UseRuntimeRules(dynamic);
/// catalog = new MessageCatalog(dynamic.Messages, appMessages, RuleMessages.Instance, BuiltInMessages.Instance);
/// </code>
/// Specs are JSON (<see cref="RuleSpecJson"/>), validated at publish against the situation schema, the group's
/// <see cref="DynamicRulePolicy"/> and the <see cref="Functions"/>, and compiled ONCE per published version: compiled rules are
/// cached in memory by (rule set code version, situation schema, function catalog version, payload hash).
/// </summary>
public sealed class DynamicRules<TSituation> : IRuntimeRuleCodec<TSituation> where TSituation : ISituation<TSituation>
{
    private const int MaxMemoryEntries = 2048;
    private readonly ConcurrentDictionary<ulong, CompiledRule<TSituation>> _compiled = new();
    private readonly ConditionalWeakTable<RuleSetDefinition<TSituation>, ExprCompileOptions> _options = new();

    public DynamicRules(FunctionCatalog? functions = null)
    {
        Functions = functions ?? FunctionCatalog.BuiltIn;
        _createOptions = s => RuleExpressions.Options(s, Functions);       // one delegate, not one closure per compile
    }

    private readonly ConditionalWeakTable<RuleSetDefinition<TSituation>, ExprCompileOptions>.CreateValueCallback _createOptions;

    public FunctionCatalog Functions { get; }

    /// <summary>Texts of the published rules (<c>messages</c> of each spec) — chain it first in the app's message catalog.</summary>
    public DynamicRuleMessages Messages { get; } = new();

    /// <summary>Compilations performed (cache misses) — diagnostics/tests.</summary>
    public int CompileCount => _compileCount;
    private int _compileCount;

    // ── IRuntimeRuleCodec ───────────────────────────────────────────────────

    public ValidationResult Validate(RuleSetDefinition<TSituation> set, string code, in RuleTierPath tierPath, ReadOnlySpan<byte> payload)
    {
        RuleSpec spec;
        try { spec = RuleSpecJson.Read(payload); }
        catch (JsonException e)
        {
            return ValidationResult.Of(new Violation(FieldPath.Of(e.Path is { Length: > 2 } p ? p[2..] : ""), DynamicRuleCodes.Malformed, detail: e.Message));
        }
        var errors = new List<Violation>();
        if (!string.Equals(spec.Code, code, StringComparison.Ordinal))
            errors.Add(new Violation(FieldPath.Of("code"), DynamicRuleCodes.CodeMismatch, detail: $"spec code '{spec.Code}' ≠ published code '{code}'"));
        if (set.FindGroup(spec.Group)?.Dynamic is { } policy && !TSituation.Schema.AllowsTier(policy.EditableAt, tierPath.Depth))
            errors.Add(new Violation(FieldPath.Of("group"), DynamicRuleCodes.TierNotAllowed,
                detail: $"group '{spec.Group}' takes runtime rules at {string.Join(", ", policy.EditableAt.ToArray())}, not at {TSituation.Schema.TierName(tierPath.Depth)}"));
        RuleCompiler<TSituation>.Compile(set, OptionsFor(set), spec, errors);
        return errors.Count == 0 ? ValidationResult.Valid : ValidationResult.Of(ViolationList.From([.. errors]));
    }

    /// <summary>The expression options of <paramref name="set"/> (situation schema, <see cref="Functions"/>, specials, rule codes) — built once per rule set.</summary>
    internal ExprCompileOptions OptionsFor(RuleSetDefinition<TSituation> set) => _options.GetValue(set, _createOptions);

    public byte[] Canonicalize(RuleSetDefinition<TSituation> set, ReadOnlySpan<byte> payload) => RuleSpecJson.Write(RuleSpecJson.Read(payload));

    public RuntimeRuleSnapshot<TSituation> Decode(RuleSetDefinition<TSituation> set, RuleDataRecord record)
    {
        var rule = GetOrCompile(set, record.Payload.Span);
        if (!string.Equals(rule.Spec.Code, record.Info.Key.Code, StringComparison.Ordinal))
            throw new ArgumentException($"Stored rule '{record.Info.Key.Code}' holds a spec for '{rule.Spec.Code}'.", nameof(record));
        Messages.Register(rule.Spec);
        return new RuntimeRuleSnapshot<TSituation>(rule.Descriptor, record.Info.Key.TierPath, record.Info.Version, record.Info.EffectiveFrom);
    }

    // ── Compilation + caches ────────────────────────────────────────────────

    /// <summary>Compiles (or reuses) a spec. Throws <see cref="ArgumentException"/> listing the problems when it doesn't fit.</summary>
    internal CompiledRule<TSituation> GetOrCompile(RuleSetDefinition<TSituation> set, ReadOnlySpan<byte> payload)
    {
        ulong key = CacheKey(set, payload);
        if (_compiled.TryGetValue(key, out var hit)) return hit;

        var spec = RuleSpecJson.Read(payload);
        var errors = new List<Violation>();
        var rule = RuleCompiler<TSituation>.Compile(set, OptionsFor(set), spec, errors);
        if (rule is null)
        {
            bool unimplemented = errors.Exists(static e => e.Code.Value == DynamicRuleCodes.Unimplemented);
            string detail = string.Join("; ", errors.ConvertAll(static e => $"{e.Path}: {e.Detail ?? e.Code.Value}"));
            throw unimplemented
                ? new NotSupportedException($"Rule '{spec.Code}' uses functions this application doesn't implement: {detail}")
                : new ArgumentException($"Rule '{spec.Code}' does not fit rule set '{set.Name}': {detail}");
        }
        Interlocked.Increment(ref _compileCount);
        if (_compiled.Count >= MaxMemoryEntries) _compiled.Clear();
        _compiled[key] = rule;
        return rule;
    }

    /// <summary>
    /// Identity of a compilation: payload + everything it was checked against (rule set code version, situation schema,
    /// function catalog). Any code/function change gives a new key → stale compilations are never reused.
    /// </summary>
    internal ulong CacheKey(RuleSetDefinition<TSituation> set, ReadOnlySpan<byte> payload)
    {
        var h = StableHash64.Create(0xD1A3);
        h.Append(set.Name);
        h.Append(unchecked((long)set.CodeVersion));
        h.Append(unchecked((long)SituationExprSchema<TSituation>.Schema.Hash));
        h.Append(unchecked((long)Functions.Version));
        h.Append(payload);
        return h.Finish();
    }

    // ── Authoring helpers ───────────────────────────────────────────────────

    /// <summary>Validates and compiles a spec object (editor/AI loops) without publishing.</summary>
    public ValidationResult Check(RuleSetDefinition<TSituation> set, RuleSpec spec, in RuleTierPath tierPath)
        => Validate(set, spec.Code, tierPath, RuleSpecJson.Write(spec));
}

/// <summary>
/// Message layer of dynamic rules (10 §4): the <c>messages</c> of each decoded spec. Key <c>"name"</c> of a spec is served
/// under the rule's code (the explanation title), other keys as written; cultures as written in the spec (<c>""</c> =
/// invariant; the catalog falls back pt-BR → pt → ""). The latest decoded version of a rule wins.
/// </summary>
public sealed class DynamicRuleMessages : IMessageSource
{
    private readonly ConcurrentDictionary<string, ImmutableDictionary<string, string>> _byCode = new(StringComparer.Ordinal);

    internal void Register(RuleSpec spec)
    {
        foreach (var (culture, messages) in spec.Messages)
            foreach (var (key, template) in messages)
            {
                string code = key == "name" ? spec.Code : key;
                _byCode.AddOrUpdate(code, _ => ImmutableDictionary<string, string>.Empty.Add(culture, template),
                    (_, d) => d.SetItem(culture, template));
            }
    }

    public bool TryGetTemplate(string code, string cultureName, out string template)
    {
        template = "";
        return _byCode.TryGetValue(code, out var byCulture) && byCulture.TryGetValue(cultureName, out template!);
    }

    public bool TryGetFieldName(string fieldKey, string cultureName, out string name)
    {
        name = "";
        return false;
    }
}
