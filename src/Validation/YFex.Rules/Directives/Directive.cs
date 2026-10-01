using System.Runtime.CompilerServices;
using YFex.Validation;

namespace YFex.Rules;

/// <summary>What happens when a <see cref="RequestedAttribute"/> value is outside a limit/options.</summary>
public enum Exceed : byte { Deny = 0, Warn = 1, RequireApproval = 2 }

/// <summary>Output column kinds of decision tables (<c>[Then]</c>).</summary>
public enum DirectiveKind : byte
{
    None = 0, Deny = 1, Warn = 2, LimitMin = 3, LimitMax = 4, Grant = 5,
    /// <summary>Decrease the target by a <see cref="Percentual"/> of its current base.</summary>
    DecreasePercent = 6,
    /// <summary>Decrease the target by an amount of the rule set's amount type.</summary>
    DecreaseAmount = 7,
    RequireApproval = 8,
    /// <summary>Increase the target by a <see cref="Percentual"/> of its current base.</summary>
    IncreasePercent = 9,
    /// <summary>Increase the target by an amount of the rule set's amount type.</summary>
    IncreaseAmount = 10,
}

/// <summary>Direction of an <see cref="AdjustDirective"/>.</summary>
public enum AdjustDirection : byte { Decrease = 0, Increase = 1 }

/// <summary>Case tag of a <see cref="Directive"/>.</summary>
public enum DirectiveCase : byte { None = 0, Deny = 1, Warn = 2, Limit = 3, Grant = 4, Adjust = 5, Approval = 6 }

/// <summary>
/// Where an adjustment applies: <see cref="Total"/> = the running total (split across lines per the rule set's
/// <see cref="Allocation"/>), <see cref="Line"/> = the current line (only valid inside line rules/tables).
/// </summary>
public readonly record struct AdjustTarget
{
    private AdjustTarget(bool line) => IsLine = line;

    public bool IsLine { get; }
    public static AdjustTarget Line => new(true);
    public static AdjustTarget Total => default;

    public override string ToString() => IsLine ? "line" : "total";
}

/// <summary>Blocks the decision. Path may be empty (object-level) → <see cref="Verdict"/> Denied.</summary>
public readonly record struct DenyDirective(Violation Violation);

/// <summary>Non-blocking message (Warned if nothing blocks).</summary>
public readonly record struct WarnDirective(Violation Violation);

/// <summary>
/// Bounds a field. Resolved per group strategy; then <see cref="RequestedAttribute"/> values are checked against it.
/// <see cref="RawValue.None"/> = unbounded side.
/// </summary>
public readonly record struct LimitDirective(SituationFieldRef Field, RawValue Min, RawValue Max,
    Exceed OnExceed = Exceed.Deny, string? ApprovalPermission = null);

/// <summary>Allowed discrete values for a field (UI picker items).</summary>
public readonly record struct GrantDirective(SituationFieldRef Field, OptionSet Options);

/// <summary>
/// Change of the rule set's amount (Money, Mass, Length, a plain number…). Either a <see cref="Percent"/> of the
/// target's current base or a fixed <see cref="Amount"/>; the ENGINE computes, caps and rounds it (rules never do the
/// math → one rounding policy). A decrease never takes the target below zero.
/// </summary>
/// <param name="Amount">Fixed change in the rule set's amount type (None when <paramref name="Percent"/> is set).</param>
/// <param name="Max">Optional cap of a percentage change, in the rule set's amount type.</param>
public readonly record struct AdjustDirective(AdjustTarget Target, AdjustDirection Direction, Percentual? Percent,
    RawValue Amount = default, RawValue Max = default)
{
    public bool IsPercent => Percent is not null;
}

/// <summary>The decision is allowed only if someone with <see cref="Permission"/> approves (manager override).</summary>
public readonly record struct ApprovalDirective(string Permission, Violation Reason);

/// <summary>
/// Closed set of rule outputs (D9) — non-boxing <c>[Union]</c> (README constraint 2). Library code switches on
/// <see cref="Case"/> and uses the typed accessors; <see cref="Value"/> boxes and exists only for <see cref="IUnion"/>.
/// </summary>
[Union]
public readonly struct Directive : IUnion
{
    private readonly Violation _violation;
    private readonly SituationFieldRef _field;
    private readonly RawValue _min;
    private readonly RawValue _max;
    private readonly OptionSet _options;
    private readonly Percentual _percent;
    private readonly string? _permission;
    private readonly AdjustTarget _target;
    private readonly Exceed _exceed;
    private readonly AdjustDirection _direction;
    private readonly bool _isPercent;
    private readonly DirectiveCase _tag;

    public Directive(DenyDirective value) : this() { _violation = value.Violation; _tag = DirectiveCase.Deny; }
    public Directive(WarnDirective value) : this() { _violation = value.Violation; _tag = DirectiveCase.Warn; }

    public Directive(LimitDirective value) : this()
    {
        _field = value.Field;
        _min = value.Min;
        _max = value.Max;
        _exceed = value.OnExceed;
        _permission = value.ApprovalPermission;
        _tag = DirectiveCase.Limit;
    }

    public Directive(GrantDirective value) : this() { _field = value.Field; _options = value.Options; _tag = DirectiveCase.Grant; }

    public Directive(AdjustDirective value) : this()
    {
        if (value.Percent is null == value.Amount.IsNone)
            throw new ArgumentException("An adjustment needs exactly one of Percent or Amount.", nameof(value));
        _target = value.Target;
        _direction = value.Direction;
        _isPercent = value.Percent is not null;
        _percent = value.Percent.GetValueOrDefault();
        _min = value.Amount;
        _max = value.Max;
        _tag = DirectiveCase.Adjust;
    }

    public Directive(ApprovalDirective value) : this() { _permission = value.Permission; _violation = value.Reason; _tag = DirectiveCase.Approval; }

    public DirectiveCase Case => _tag;
    public bool HasValue => _tag != DirectiveCase.None;

    /// <summary>Boxes — required by <see cref="IUnion"/>; never call it in library code.</summary>
    public object? Value => _tag switch
    {
        DirectiveCase.Deny => new DenyDirective(_violation),
        DirectiveCase.Warn => new WarnDirective(_violation),
        DirectiveCase.Limit => new LimitDirective(_field, _min, _max, _exceed, _permission),
        DirectiveCase.Grant => new GrantDirective(_field, _options),
        DirectiveCase.Adjust => AdjustValue,
        DirectiveCase.Approval => new ApprovalDirective(_permission!, _violation),
        _ => null,
    };

    public bool TryGetValue(out DenyDirective value) { value = _tag == DirectiveCase.Deny ? new(_violation) : default; return _tag == DirectiveCase.Deny; }
    public bool TryGetValue(out WarnDirective value) { value = _tag == DirectiveCase.Warn ? new(_violation) : default; return _tag == DirectiveCase.Warn; }
    public bool TryGetValue(out LimitDirective value) { value = _tag == DirectiveCase.Limit ? new(_field, _min, _max, _exceed, _permission) : default; return _tag == DirectiveCase.Limit; }
    public bool TryGetValue(out GrantDirective value) { value = _tag == DirectiveCase.Grant ? new(_field, _options) : default; return _tag == DirectiveCase.Grant; }
    public bool TryGetValue(out AdjustDirective value) { value = _tag == DirectiveCase.Adjust ? AdjustValue : default; return _tag == DirectiveCase.Adjust; }

    private AdjustDirective AdjustValue => new(_target, _direction, _isPercent ? _percent : null, _min, _max);
    public bool TryGetValue(out ApprovalDirective value) { value = _tag == DirectiveCase.Approval ? new(_permission!, _violation) : default; return _tag == DirectiveCase.Approval; }

    public static implicit operator Directive(DenyDirective v) => new(v);
    public static implicit operator Directive(WarnDirective v) => new(v);
    public static implicit operator Directive(LimitDirective v) => new(v);
    public static implicit operator Directive(GrantDirective v) => new(v);
    public static implicit operator Directive(AdjustDirective v) => new(v);
    public static implicit operator Directive(ApprovalDirective v) => new(v);

    // ── Non-copying accessors for the engine ─────────────────────────────────
    internal Violation Violation => _violation;
    internal SituationFieldRef Field => _field;
    internal RawValue Min => _min;
    internal RawValue Max => _max;
    internal OptionSet Options => _options;
    internal bool IsPercent => _isPercent;
    internal Percentual Percent => _percent;
    /// <summary>Adjust: fixed change (None for percentages).</summary>
    internal RawValue Amount => _min;
    /// <summary>Adjust: cap of a percentage change (None = no cap).</summary>
    internal RawValue MaxAmount => _max;
    internal AdjustDirection Direction => _direction;
    internal string? Permission => _permission;
    internal AdjustTarget Target => _target;
    internal Exceed OnExceed => _exceed;

    public override string ToString() => _tag switch
    {
        DirectiveCase.Deny => $"Deny {_violation}",
        DirectiveCase.Warn => $"Warn {_violation}",
        DirectiveCase.Limit => $"Limit {_field.Name} [{(_min.IsNone ? "" : _min.ToString())}..{(_max.IsNone ? "" : _max.ToString())}] {_exceed}",
        DirectiveCase.Grant => $"Grant {_field.Name} ({_options.Count} options)",
        DirectiveCase.Adjust => $"{_direction} {_target} {(_isPercent ? _percent.ToString() : _min.ToString())}",
        DirectiveCase.Approval => $"RequireApproval {_permission}",
        _ => "None",
    };
}

/// <summary>
/// Optional bound for <see cref="DirectiveWriter.Limit{TS,T}"/>: lets <c>d.Limit(field, max: 6)</c> distinguish
/// "no minimum" from a minimum of <c>0</c> (an unconstrained <c>T?</c> can't). Implicit from <typeparamref name="T"/>.
/// </summary>
public readonly struct LimitBound<T>
{
    private LimitBound(T value) { Value = value; HasValue = true; }

    public T Value { get; }
    public bool HasValue { get; }

    public static implicit operator LimitBound<T>(T value) => new(value);

    internal RawValue ToRaw() => HasValue ? RawValueConvert.From(Value) : RawValue.None;
}

/// <summary>Hidden helper so generated code can stamp origins without depending on internals.</summary>
internal static class DirectiveOrigins
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ViolationOrigin For(int ruleIndex) => new(ViolationSource.Rule, (ushort)ruleIndex);
}
