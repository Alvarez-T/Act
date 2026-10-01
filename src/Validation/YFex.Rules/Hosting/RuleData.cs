using System.Collections.Immutable;

namespace YFex.Rules;

/// <summary>Kind of a <see cref="RuleOverrideAction"/>.</summary>
public enum RuleOverrideKind : byte { None = 0, Disable = 1, ReplaceWith = 2, Enable = 3 }

/// <summary>Case: turn a non-sealed rule/table off for the scope.</summary>
public readonly record struct DisableRule;
/// <summary>Case: turn the rule off and let the table/dynamic rule with <see cref="Code"/> take its place.</summary>
public readonly record struct ReplaceRuleWith(string Code);
/// <summary>Case: a more specific layer cancels a less specific Disable/Replace.</summary>
public readonly record struct EnableRule;

/// <summary>Override published for a rule code (05 §6). Forbidden (and ignored if stored anyway) for <see cref="SealedAttribute"/> rules.</summary>
[Union]
public readonly struct RuleOverrideAction : IUnion
{
    private readonly string? _code;
    private readonly RuleOverrideKind _tag;

    public RuleOverrideAction(DisableRule _) : this() => _tag = RuleOverrideKind.Disable;
    public RuleOverrideAction(ReplaceRuleWith v) : this() { _code = v.Code; _tag = RuleOverrideKind.ReplaceWith; }
    public RuleOverrideAction(EnableRule _) : this() => _tag = RuleOverrideKind.Enable;

    public static RuleOverrideAction Disable => new(new DisableRule());
    public static RuleOverrideAction Enable => new(new EnableRule());
    public static RuleOverrideAction ReplaceWith(string code) => new(new ReplaceRuleWith(code));

    public RuleOverrideKind Kind => _tag;
    public bool HasValue => _tag != RuleOverrideKind.None;
    public string? ReplacementCode => _code;

    /// <summary>Boxes (IUnion) — not for library code.</summary>
    public object? Value => _tag switch
    {
        RuleOverrideKind.Disable => new DisableRule(),
        RuleOverrideKind.ReplaceWith => new ReplaceRuleWith(_code!),
        RuleOverrideKind.Enable => new EnableRule(),
        _ => null,
    };

    public bool TryGetValue(out DisableRule v) { v = default; return _tag == RuleOverrideKind.Disable; }
    public bool TryGetValue(out ReplaceRuleWith v) { v = new(_code!); return _tag == RuleOverrideKind.ReplaceWith; }
    public bool TryGetValue(out EnableRule v) { v = default; return _tag == RuleOverrideKind.Enable; }
}

/// <summary>
/// Immutable published content of ONE table / parameter set / override for ONE scope, effective from a date
/// (04 §10). Never edited — changes create a new version.
/// </summary>
public abstract class RulesetSnapshot(string code, RuleTierPath tierPath, long version, DateOnly effectiveFrom)
{
    /// <summary>Table code (tables) or rule code (parameters, overrides).</summary>
    public string Code { get; } = code;
    /// <summary>Where it was published.</summary>
    public RuleTierPath TierPath { get; } = tierPath;
    public long Version { get; } = version;
    public DateOnly EffectiveFrom { get; } = effectiveFrom;
    /// <summary>Depth of <see cref="TierPath"/> (0 = code).</summary>
    public int Tier => TierPath.Depth;
}

/// <summary>A published table version.</summary>
public sealed class TableSnapshot(TableVersion table) : RulesetSnapshot(table.TableCode, table.TierPath, table.Version, table.EffectiveFrom)
{
    public TableVersion Table { get; } = table;
}

/// <summary>Published values of a code rule's parameters (by parameter name). Values box here — publish time only.</summary>
public sealed class ParameterSnapshot(string ruleCode, RuleTierPath tierPath, long version, DateOnly effectiveFrom, ImmutableDictionary<string, object?> values)
    : RulesetSnapshot(ruleCode, tierPath, version, effectiveFrom)
{
    public ImmutableDictionary<string, object?> Values { get; } = values;
}

/// <summary>A published rule override.</summary>
public sealed class OverrideSnapshot(string ruleCode, RuleTierPath tierPath, long version, DateOnly effectiveFrom, RuleOverrideAction action)
    : RulesetSnapshot(ruleCode, tierPath, version, effectiveFrom)
{
    public RuleOverrideAction Action { get; } = action;
}

/// <summary>Everything published for a rule set (immutable; the host swaps it atomically).</summary>
public sealed class RuleData
{
    private RuleData(ImmutableArray<RulesetSnapshot> snapshots) => Snapshots = snapshots;

    public static RuleData Empty { get; } = new([]);

    public ImmutableArray<RulesetSnapshot> Snapshots { get; }

    public static RuleData Of(params ReadOnlySpan<RulesetSnapshot> snapshots) => new([.. snapshots]);

    /// <summary>Adds <paramref name="snapshot"/>, keeping at most <paramref name="keepPerCode"/> versions per (kind, code, tier path).</summary>
    public RuleData With(RulesetSnapshot snapshot, int keepPerCode = int.MaxValue)
    {
        var b = ImmutableArray.CreateBuilder<RulesetSnapshot>(Snapshots.Length + 1);
        int same = 1;
        for (int i = Snapshots.Length - 1; i >= 0; i--)
        {
            var s = Snapshots[i];
            if (s.GetType() == snapshot.GetType() && s.Code == snapshot.Code && s.TierPath == snapshot.TierPath)
            {
                if (s.Version == snapshot.Version) continue;          // republishing the same version replaces it
                if (same++ >= keepPerCode) continue;
            }
            b.Add(s);
        }
        b.Reverse();
        b.Add(snapshot);
        return new(b.ToImmutable());
    }

    /// <summary>Removes every version of <paramref name="like"/>'s stream (same kind, code, tier path) — what-if drafts.</summary>
    public RuleData Without(RulesetSnapshot like)
    {
        var b = ImmutableArray.CreateBuilder<RulesetSnapshot>(Snapshots.Length);
        foreach (var s in Snapshots)
            if (s.GetType() != like.GetType() || s.Code != like.Code || s.TierPath != like.TierPath) b.Add(s);
        return b.Count == Snapshots.Length ? this : new(b.ToImmutable());
    }

    /// <summary>
    /// The snapshot of type <typeparamref name="T"/> for <paramref name="code"/> in effect on <paramref name="effectiveDate"/>
    /// at the exact <paramref name="tierPath"/>: latest EffectiveFrom ≤ effectiveDate, then highest version.
    /// </summary>
    public T? InForce<T>(string code, in RuleTierPath tierPath, DateOnly effectiveDate) where T : RulesetSnapshot
    {
        T? best = null;
        foreach (var s in Snapshots)
        {
            if (s is not T t || t.Code != code || t.TierPath != tierPath || t.EffectiveFrom > effectiveDate) continue;
            if (best is null || t.EffectiveFrom > best.EffectiveFrom || (t.EffectiveFrom == best.EffectiveFrom && t.Version > best.Version)) best = t;
        }
        return best;
    }
}
