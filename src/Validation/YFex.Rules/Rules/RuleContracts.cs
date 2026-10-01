namespace YFex.Rules;

/// <summary>
/// A business rule over a situation (04 §3). Marker interface: the generator looks for these static methods and
/// adapts them (both shapes are accepted for each, so a rule only asks for the context when it needs it):
/// <code>
/// static bool Applies(in TSituation s)                          | Applies(in TSituation s, in RuleContext c)
/// static void Evaluate(in TSituation s, ref DirectiveWriter d)  | Evaluate(in TSituation s, in RuleContext c, ref DirectiveWriter d)
/// </code>
/// Calls are direct static calls (inlinable) — rules are never objects. Missing/mistyped methods = YFR0010.
/// </summary>
public interface IRule<TSituation> where TSituation : ISituation<TSituation>;

/// <summary>
/// Line-level rule (evaluated once per line whose <c>Applies</c> is true). Methods take the line after the situation:
/// <c>Applies(in TS s, in TLine line[, in RuleContext c])</c>, <c>Evaluate(in TS s, in TLine line[, in RuleContext c], ref DirectiveWriter d)</c>.
/// </summary>
public interface IRule<TSituation, TLine>
    where TSituation : ISituation<TSituation> where TLine : ISituation<TLine>;

/// <summary>
/// Declares a code rule. <paramref name="code"/> is stable (persisted/audited, message key root).
/// <see cref="Version"/> = bump manually when the LOGIC changes (parameters/tables have their own versions).
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class RuleAttribute(string code) : Attribute
{
    public string Code { get; } = code;
    /// <summary>Name of the <see cref="RuleGroup"/> field of the rule set (<c>nameof(SaleRules.Loyalty)</c>).</summary>
    public string? Group { get; init; }
    /// <summary>Higher first (First strategy, tie-breaks). Ties → rule code ordinal.</summary>
    public int Priority { get; init; }
    public int Version { get; init; } = 1;
    /// <summary>Name of the rule set (<c>"sale"</c>) — only needed when several rule sets of the same situation declare the group.</summary>
    public string? RuleSet { get; init; }
}

/// <summary>Optional validity window compared to the situation's <see cref="RuleEffectiveDateAttribute"/> field (ISO dates, inclusive).</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class EffectiveAttribute : Attribute
{
    /// <summary><c>"2026-01-01"</c>.</summary>
    public string? From { get; init; }
    /// <summary><c>"2026-12-31"</c> (inclusive).</summary>
    public string? To { get; init; }
}

/// <summary>No rule tier may disable, replace or loosen this rule (D12).</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, Inherited = false)]
public sealed class SealedAttribute : Attribute;

/// <summary>
/// Declares a rule set over <typeparamref name="TSituation"/> on a <c>static partial class</c> holding its
/// <see cref="Stage"/>s, <see cref="RuleGroup"/>s, <c>Rounding</c>, <c>Allocation</c> and <see cref="DerivedField{TSituation,T}"/>s.
/// Optional amount: <c>static TAmount Base(in TS)</c> or <c>static TAmount LineBase(in TS, in TLine)</c> — any
/// <see cref="IAmount{TSelf}"/> (Money, Mass, Length…) or plain decimal/int/long/double; without one the rule set only
/// limits, grants, denies, warns and requires approvals (Increase/Decrease need an amount).
/// The generator emits <c>Set</c>, <c>Evaluate</c>, <c>Catalog</c> and <c>RuleIds</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RuleSetAttribute<TSituation>(string name) : Attribute where TSituation : ISituation<TSituation>
{
    public string Name { get; } = name;
}

/// <summary>
/// Read-only evaluation context (ref struct: cannot escape). What is NOT in the situation but is part of the
/// evaluation itself: the running base, the line, parameter values for the current tier path/version.
/// </summary>
public readonly ref struct RuleContext
{
    private readonly ParameterValue?[]? _parameters;

    internal RuleContext(DateOnly effectiveDate, RawValue @base, int lineIndex, ParameterValue?[]? parameters)
    {
        EffectiveDate = effectiveDate;
        Base = @base;
        LineIndex = lineIndex;
        _parameters = parameters;
    }

    /// <summary>The situation's <see cref="RuleEffectiveDateAttribute"/> field.</summary>
    public DateOnly EffectiveDate { get; }
    /// <summary>
    /// Running amount of the current target (line base in line rules, running total otherwise), typed as the rule set's
    /// amount (<c>c.Base.As&lt;Mass&gt;()</c>); <see cref="RawValue.None"/> when the rule set has no amount.
    /// </summary>
    public RawValue Base { get; }
    /// <summary>−1 outside line rules.</summary>
    public int LineIndex { get; }

    /// <summary>Value of <paramref name="parameter"/> for the current scope/version (default when not overridden).</summary>
    public T Get<T>(RuleParameter<T> parameter)
        => _parameters is { } p && (uint)parameter.Slot < (uint)p.Length && p[parameter.Slot] is ParameterValue<T> v
            ? v.Value
            : parameter.Default;
}
