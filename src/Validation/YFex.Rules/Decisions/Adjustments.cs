namespace YFex.Rules;

/// <summary>Per-line share of a total-level adjustment (D32), in the rule set's amount type (magnitude ≥ 0).</summary>
public readonly record struct LineAllocation(int LineIndex, RawValue Amount);

/// <summary>Case of <see cref="ResolutionNote"/>: reduced by a Stack cap or a directive's Max.</summary>
public readonly record struct CappedBy(RuleRef CapOwner, RawValue Requested, RawValue Applied);
/// <summary>Case of <see cref="ResolutionNote"/>: applied together with other rules of a Stack group.</summary>
public readonly record struct StackedWith(RuleRef Other);
/// <summary>Case of <see cref="ResolutionNote"/>: a value (parameter/row) was clamped by a sealed guard.</summary>
public readonly record struct ClampedByGuardrail(RuleRef Guard);
/// <summary>Case of <see cref="ResolutionNote"/>: applied under an approval.</summary>
public readonly record struct ApprovedBy(string UserId, string Permission);

/// <summary>Kind of a <see cref="ResolutionNote"/>.</summary>
public enum ResolutionNoteKind : byte { None = 0, CappedBy = 1, StackedWith = 2, ClampedByGuardrail = 3, ApprovedBy = 4 }

/// <summary>Why the amount of an applied adjustment may differ from what the rule asked for (06 §2). Non-boxing union.</summary>
[Union]
public readonly struct ResolutionNote : IUnion
{
    private readonly RuleRef _ref;
    private readonly RawValue _requested;
    private readonly RawValue _applied;
    private readonly string? _user;
    private readonly string? _permission;
    private readonly ResolutionNoteKind _tag;

    public ResolutionNote(CappedBy v) : this() { _ref = v.CapOwner; _requested = v.Requested; _applied = v.Applied; _tag = ResolutionNoteKind.CappedBy; }
    public ResolutionNote(StackedWith v) : this() { _ref = v.Other; _tag = ResolutionNoteKind.StackedWith; }
    public ResolutionNote(ClampedByGuardrail v) : this() { _ref = v.Guard; _tag = ResolutionNoteKind.ClampedByGuardrail; }
    public ResolutionNote(ApprovedBy v) : this() { _user = v.UserId; _permission = v.Permission; _tag = ResolutionNoteKind.ApprovedBy; }

    public ResolutionNoteKind Kind => _tag;
    public bool HasValue => _tag != ResolutionNoteKind.None;

    /// <summary>Boxes (IUnion) — not for library code.</summary>
    public object? Value => _tag switch
    {
        ResolutionNoteKind.CappedBy => new CappedBy(_ref, _requested, _applied),
        ResolutionNoteKind.StackedWith => new StackedWith(_ref),
        ResolutionNoteKind.ClampedByGuardrail => new ClampedByGuardrail(_ref),
        ResolutionNoteKind.ApprovedBy => new ApprovedBy(_user!, _permission!),
        _ => null,
    };

    public bool TryGetValue(out CappedBy v) { v = _tag == ResolutionNoteKind.CappedBy ? new(_ref, _requested, _applied) : default; return _tag == ResolutionNoteKind.CappedBy; }
    public bool TryGetValue(out StackedWith v) { v = _tag == ResolutionNoteKind.StackedWith ? new(_ref) : default; return _tag == ResolutionNoteKind.StackedWith; }
    public bool TryGetValue(out ClampedByGuardrail v) { v = _tag == ResolutionNoteKind.ClampedByGuardrail ? new(_ref) : default; return _tag == ResolutionNoteKind.ClampedByGuardrail; }
    public bool TryGetValue(out ApprovedBy v) { v = _tag == ResolutionNoteKind.ApprovedBy ? new(_user!, _permission!) : default; return _tag == ResolutionNoteKind.ApprovedBy; }

    public static implicit operator ResolutionNote(CappedBy v) => new(v);
    public static implicit operator ResolutionNote(StackedWith v) => new(v);
    public static implicit operator ResolutionNote(ClampedByGuardrail v) => new(v);
    public static implicit operator ResolutionNote(ApprovedBy v) => new(v);
}

/// <summary>
/// One applied change of the rule set's amount — always recorded (business data, D17), persisted with the document:
/// everything needed to print it, audit it and rebuild per-line figures (06 §2). Amounts are typed
/// <see cref="RawValue"/>s of the rule set's amount type: <c>a.Amount.As&lt;Money&gt;()</c>.
/// </summary>
public readonly struct AppliedAdjustment
{
    private readonly LineAllocation[]? _allocations;

    public AppliedAdjustment(RuleRef rule, StageRef stage, AdjustTarget target, int lineIndex, int order, AdjustDirection direction,
        Percentual? percent, RawValue @base, RawValue amount, ResolutionNote note, LineAllocation[]? allocations)
    {
        Rule = rule;
        Stage = stage;
        Target = target;
        LineIndex = lineIndex;
        Order = order;
        Direction = direction;
        Percent = percent;
        Base = @base;
        Amount = amount;
        Note = note;
        _allocations = allocations;
    }

    public RuleRef Rule { get; }
    public StageRef Stage { get; }
    public AdjustTarget Target { get; }
    /// <summary>Line index for <see cref="AdjustTarget.Line"/>; −1 for total-level adjustments.</summary>
    public int LineIndex { get; }
    /// <summary>Application order (1..n) — explains stacking.</summary>
    public int Order { get; }
    public AdjustDirection Direction { get; }
    /// <summary>The percentage the rule asked for; null for a fixed amount.</summary>
    public Percentual? Percent { get; }
    /// <summary>Amount it was computed from at that moment.</summary>
    public RawValue Base { get; }
    /// <summary>Resulting (rounded, capped) size of the change, ≥ 0 — see <see cref="Direction"/>.</summary>
    public RawValue Amount { get; }
    public ResolutionNote Note { get; }
    /// <summary>Total-level adjustments split per line (empty with <see cref="Allocation.None"/> or line targets).</summary>
    public ReadOnlySpan<LineAllocation> Allocations => _allocations;

    public override string ToString()
        => $"#{Order} {Rule.Code} {Direction} {Target}{(LineIndex >= 0 ? "[" + LineIndex + "]" : "")} "
           + $"{(Percent is { } p ? p + " of " + Base : "")} = {Amount}";
}

/// <summary>Exact-size list of applied adjustments with a struct enumerator.</summary>
public readonly struct AdjustmentList
{
    private readonly AppliedAdjustment[]? _items;

    internal AdjustmentList(AppliedAdjustment[]? items) => _items = items is { Length: > 0 } ? items : null;

    public static AdjustmentList Empty => default;
    public int Count => _items?.Length ?? 0;
    public bool IsEmpty => _items is null;
    public ref readonly AppliedAdjustment this[int index] => ref _items![index];
    public ReadOnlySpan<AppliedAdjustment> AsSpan() => _items;
    public ReadOnlySpan<AppliedAdjustment>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();
}

/// <summary>
/// Per-line figures after all stages, in the rule set's amount type. Changes are signed (<c>Final − Initial</c>):
/// negative after decreases, positive after increases.
/// </summary>
/// <param name="LineChange">Net change from line-level adjustments.</param>
/// <param name="AllocatedChange">Net change allocated from total-level adjustments.</param>
public readonly record struct LineTotal(int LineIndex, RawValue Initial, RawValue LineChange, RawValue AllocatedChange, RawValue Final);

/// <summary>Totals of a decision, in the rule set's amount type (<c>d.Totals.Final.As&lt;Money&gt;()</c>).</summary>
public readonly struct DecisionTotals
{
    private readonly LineTotal[]? _lines;

    internal DecisionTotals(RawValue initial, RawValue final, RawValue change, LineTotal[]? lines)
    {
        Initial = initial;
        Final = final;
        Change = change;
        _lines = lines;
    }

    /// <summary>Σ line bases (or the situation base) before any adjustment. None when the rule set has no amount.</summary>
    public RawValue Initial { get; }
    /// <summary>After every adjustment.</summary>
    public RawValue Final { get; }
    /// <summary><c>Final − Initial</c> (signed).</summary>
    public RawValue Change { get; }
    /// <summary>Per line: initial, line-level change, allocated change, final.</summary>
    public ReadOnlySpan<LineTotal> Lines => _lines;
}
