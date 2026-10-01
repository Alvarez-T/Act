namespace YFex.Rules;

/// <summary>
/// Ordered phase of evaluation (e.g. Items → Totals → Limits). Order is explicit and total; each stage's
/// base is the running amount left by the previous one.
/// <code>
/// public static readonly Stage Items    = Stage.First("items");
/// public static readonly Stage Totals   = Stage.After(Items, "totals");
/// </code>
/// </summary>
public sealed class Stage
{
    private Stage(byte order, string code) => Ref = new(order, code);

    public StageRef Ref { get; }
    public byte Order => Ref.Order;
    public string Code => Ref.Code;

    public static Stage First(string code) => new(0, code);

    public static Stage After(Stage previous, string code)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (previous.Order == byte.MaxValue) throw new InvalidOperationException("Too many stages.");
        return new((byte)(previous.Order + 1), code);
    }

    public override string ToString() => Code;
}

/// <summary>Persistable stage reference (by code).</summary>
public readonly record struct StageRef(byte Order, string Code)
{
    public override string ToString() => Code;
}

/// <summary>
/// Rules competing for the same thing + how the conflict is resolved (04 §4). The name is the declaring field's
/// name (the generator passes <c>nameof</c>), used by <c>[Rule(Group = …)]</c> and tables.
/// </summary>
public sealed class RuleGroup(Stage stage, Resolve resolve)
{
    public Stage Stage { get; } = stage ?? throw new ArgumentNullException(nameof(stage));
    public Resolve Resolve { get; } = resolve;
    /// <summary>Set when the rule set definition is built.</summary>
    public string Name { get; internal set; } = "";
    /// <summary>Declaration index inside the rule set (stable iteration order).</summary>
    public int Index { get; internal set; } = -1;

    /// <summary>
    /// Opt-in: runtime-authored rules (<c>YFex.Rules.Dynamic</c>, Composer, AI) may join this group — at which tiers, with
    /// which directives and under which caps. Null (default) = code rules and tables only.
    /// </summary>
    public DynamicRulePolicy? Dynamic { get; init; }

    public override string ToString() => Name;
}

/// <summary>Directive families a <see cref="DynamicRulePolicy"/> allows.</summary>
[Flags]
public enum DynamicDirectives : ushort
{
    None = 0,
    Deny = 1,
    Warn = 2,
    Limit = 4,
    Grant = 8,
    Decrease = 16,
    Increase = 32,
    RequireApproval = 64,
    /// <summary>Everything except adjustments (no money moves).</summary>
    Constraints = Deny | Warn | Limit | Grant | RequireApproval,
    All = Constraints | Decrease | Increase,
}

/// <summary>
/// Guardrails of runtime rules in one group (05 §8, D12): tier names that may publish them (like a table's
/// <c>EditableAt</c>), allowed directive families, caps for adjustments (a dynamic rule asking for more is clamped at
/// evaluation and rejected at publish) and a size limit.
/// <code>public static readonly RuleGroup PaymentDisc = new(Main, Resolve.All)
///     { Dynamic = new([nameof(Sale.Tenant)], DynamicDirectives.Decrease, maxPercent: Percentual.FromPercentage(10)) };</code>
/// </summary>
public sealed class DynamicRulePolicy(string[] editableAt, DynamicDirectives allow = DynamicDirectives.Constraints,
    Percentual? maxPercent = null, decimal? maxAmount = null, int maxRules = 50)
{
    private readonly string[] _editableAt = editableAt ?? throw new ArgumentNullException(nameof(editableAt));

    /// <summary>Names of the situation's <see cref="RuleTierAttribute"/> fields that may publish rules into the group.</summary>
    public ReadOnlySpan<string> EditableAt => _editableAt;
    public DynamicDirectives Allow { get; } = allow;
    /// <summary>Cap of a percent Increase/Decrease.</summary>
    public Percentual? MaxPercent { get; } = maxPercent;
    /// <summary>Cap of an amount Increase/Decrease (decimal storage of the rule set's amount type).</summary>
    public decimal? MaxAmount { get; } = maxAmount;
    /// <summary>Most dynamic rules in force for one tier path in this group.</summary>
    public int MaxRules { get; } = maxRules;
}

/// <summary>Resolution strategy kinds (04 §4 table).</summary>
public enum ResolveKind : byte { First = 0, All = 1, MostRestrictive = 2, LeastRestrictive = 3, Stack = 4, Exclusive = 5 }

/// <summary>Which candidate <see cref="Resolve.Exclusive"/> keeps: the one whose adjustments change the amount the most or the least.</summary>
public enum Prefer : byte { Largest = 0, Smallest = 1 }

/// <summary>Resolution strategy of a <see cref="RuleGroup"/>.</summary>
public readonly record struct Resolve
{
    private Resolve(ResolveKind kind, Prefer prefer = default, Percentual? cap = null, RawValue capAmount = default)
    {
        Kind = kind;
        Prefer = prefer;
        Cap = cap;
        CapAmount = capAmount;
    }

    public ResolveKind Kind { get; }
    public Prefer Prefer { get; }
    /// <summary><see cref="ResolveKind.Stack"/>: maximum total change of the group, as a percentage of its base.</summary>
    public Percentual? Cap { get; }
    /// <summary><see cref="ResolveKind.Stack"/>: maximum total change of the group in the rule set's amount (None = no cap).</summary>
    public RawValue CapAmount { get; }

    /// <summary>Highest-priority applicable rule wins; the others are Superseded.</summary>
    public static Resolve First => new(ResolveKind.First);
    /// <summary>Every applicable rule's directives apply (legal/fiscal checks, warnings).</summary>
    public static Resolve All => new(ResolveKind.All);
    /// <summary>Limits: max of mins / min of maxes; grants: intersection; adjustments: the smallest one.</summary>
    public static Resolve MostRestrictive => new(ResolveKind.MostRestrictive);
    /// <summary>Limits: min of mins / max of maxes; grants: union; adjustments: the largest one.</summary>
    public static Resolve LeastRestrictive => new(ResolveKind.LeastRestrictive);
    /// <summary>Adjustments add up (each from the group's base); the total change is capped; capped rules traced as Capped.</summary>
    public static Resolve Stack(Percentual? cap = null) => new(ResolveKind.Stack, cap: cap);
    /// <summary><see cref="Stack(Percentual?)"/> with an absolute cap in the rule set's amount (<c>Stack(capAmount: Mass.FromKilograms(40))</c>).</summary>
    public static Resolve Stack<TAmount>(TAmount capAmount, Percentual? cap = null) where TAmount : struct
        => new(ResolveKind.Stack, cap: cap, capAmount: RawValueConvert.From(capAmount));
    /// <summary>Evaluate all candidates, keep the single one whose adjustments change the amount the most/least.</summary>
    public static Resolve Exclusive(Prefer prefer = Prefer.Largest) => new(ResolveKind.Exclusive, prefer);
}

/// <summary>How total-level adjustments are split across lines (D32).</summary>
public enum AllocationKind : byte { Proportional = 0, None = 1 }

/// <summary>Where the rounding remainder of a proportional allocation goes.</summary>
public enum Remainder : byte { LargestLine = 0 }

/// <summary>
/// Declarative allocation option of a rule set (D32): <c>public static Allocation Allocation => Allocation.None;</c>.
/// Default (when omitted) = <see cref="Proportional"/> with <see cref="Remainder.LargestLine"/>. The sum of
/// allocations always equals the adjustment exactly; ties of the largest line go to the lowest index.
/// </summary>
public readonly record struct Allocation(AllocationKind Kind, Remainder Remainder = Remainder.LargestLine)
{
    public static Allocation Proportional(Remainder remainder = Remainder.LargestLine) => new(AllocationKind.Proportional, remainder);
    public static Allocation None => new(AllocationKind.None);
}
