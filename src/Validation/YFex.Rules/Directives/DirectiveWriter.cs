using System.Buffers;
using YFex.Validation;
using YFex.Authorization.Public;

namespace YFex.Rules;

/// <summary>
/// Stack-only writer the engine passes to <c>Evaluate</c> (04 §5). A rule never mutates anything; it issues
/// directives, stamped with the rule (candidate) that wrote them; the engine resolves them per group.
/// <code>
/// d.Limit(Shipment.Fields.DeclaredWeight, max: Mass.FromKilograms(70));
/// d.Increase(AdjustTarget.Total, c.Get(PalletTare));            // fixed amount of the rule set's amount type
/// d.Decrease(AdjustTarget.Line, Percentual.FromPercentage(10));  // % of the current base
/// d.Deny(FieldPath.Of("Destination"), "freight.destination.blocked");
/// </code>
/// </summary>
public ref struct DirectiveWriter
{
    private readonly DirectiveBuffer _buffer;
    private readonly int _candidate;
    private readonly ushort _owner;

    internal DirectiveWriter(DirectiveBuffer buffer, int candidate, ushort owner)
    {
        _buffer = buffer;
        _candidate = candidate;
        _owner = owner;
    }

    /// <summary>Directives written through this writer so far.</summary>
    public readonly int Count => _buffer.CountFor(_candidate);

    public void Deny(in FieldPath path, RuleCode code) => Add(new DenyDirective(Make(path, code, Severity.Error, default)));
    public void Deny(in FieldPath path, RuleCode code, RawValue a0) => Add(new DenyDirective(Make(path, code, Severity.Error, new RuleArgs(a0))));
    public void Deny(in FieldPath path, RuleCode code, RawValue a0, RawValue a1) => Add(new DenyDirective(Make(path, code, Severity.Error, new RuleArgs(a0, a1))));

    public void Warn(in FieldPath path, RuleCode code) => Add(new WarnDirective(Make(path, code, Severity.Warning, default)));
    public void Warn(in FieldPath path, RuleCode code, RawValue a0) => Add(new WarnDirective(Make(path, code, Severity.Warning, new RuleArgs(a0))));
    public void Warn(in FieldPath path, RuleCode code, RawValue a0, RawValue a1) => Add(new WarnDirective(Make(path, code, Severity.Warning, new RuleArgs(a0, a1))));

    /// <summary>Bounds a situation field: <c>d.Limit(Fields.Installments, max: 6)</c>. Omitted side = unbounded.</summary>
    public void Limit<TS, T>(SituationField<TS, T> field, LimitBound<T> min = default, LimitBound<T> max = default,
        Exceed onExceed = Exceed.Deny, Permission? permission = null) where TS : ISituation<TS>
        => Add(new LimitDirective(field, min.ToRaw(), max.ToRaw(), onExceed, permission));

    /// <summary>Bounds a rule-set derived field: <c>d.Limit(SaleRules.InstallmentValue, min: new Money(5))</c>.</summary>
    public void Limit<TS, T>(DerivedField<TS, T> field, LimitBound<T> min = default, LimitBound<T> max = default,
        Exceed onExceed = Exceed.Deny, Permission? permission = null) where TS : ISituation<TS>
        => Add(new LimitDirective(field.Ref, min.ToRaw(), max.ToRaw(), onExceed, permission));

    /// <summary>Untyped limit (tables, dynamic rules). <see cref="RawValue.None"/> = unbounded side.</summary>
    public void LimitRaw(SituationFieldRef field, RawValue min, RawValue max, Exceed onExceed = Exceed.Deny, Permission? permission = null)
        => Add(new LimitDirective(field, min, max, onExceed, permission));

    /// <summary>Allowed discrete values: <c>d.Grant(Fields.Installments, OptionSet.Range(1, 6))</c>.</summary>
    public void Grant<TS, T>(SituationField<TS, T> field, in OptionSet options) where TS : ISituation<TS>
        => Add(new GrantDirective(field, options));

    public void Grant(SituationFieldRef field, in OptionSet options) => Add(new GrantDirective(field, options));

    // ── Adjustments (need a rule set amount: Base/LineBase) ────────────────────────────────────────────────────

    /// <summary>Decreases the target by <paramref name="percent"/> of its current base (never below zero).</summary>
    public void Decrease(AdjustTarget target, Percentual percent)
        => Add(new AdjustDirective(target, AdjustDirection.Decrease, percent));

    /// <summary>Decreases by <paramref name="percent"/> of the base, at most <paramref name="max"/>.</summary>
    public void Decrease<TAmount>(AdjustTarget target, Percentual percent, TAmount max) where TAmount : IAmount<TAmount>
        => Add(new AdjustDirective(target, AdjustDirection.Decrease, percent, Max: max.ToRaw()));

    /// <summary>Decreases the target by a fixed amount of the rule set's amount type (Money, Mass…).</summary>
    public void Decrease<TAmount>(AdjustTarget target, TAmount amount) where TAmount : IAmount<TAmount>
        => Add(new AdjustDirective(target, AdjustDirection.Decrease, null, amount.ToRaw()));

    /// <summary>Decreases a plain-number amount (decimal/int/long rule sets).</summary>
    public void Decrease(AdjustTarget target, decimal amount) => Add(new AdjustDirective(target, AdjustDirection.Decrease, null, RawValue.From(amount)));
    /// <inheritdoc cref="Decrease(AdjustTarget, decimal)"/>
    public void Decrease(AdjustTarget target, long amount) => Add(new AdjustDirective(target, AdjustDirection.Decrease, null, RawValue.From(amount)));
    /// <inheritdoc cref="Decrease(AdjustTarget, decimal)"/>
    public void Decrease(AdjustTarget target, double amount) => Add(new AdjustDirective(target, AdjustDirection.Decrease, null, RawValue.From(amount)));

    /// <summary>Increases the target by <paramref name="percent"/> of its current base.</summary>
    public void Increase(AdjustTarget target, Percentual percent)
        => Add(new AdjustDirective(target, AdjustDirection.Increase, percent));

    /// <summary>Increases by <paramref name="percent"/> of the base, at most <paramref name="max"/>.</summary>
    public void Increase<TAmount>(AdjustTarget target, Percentual percent, TAmount max) where TAmount : IAmount<TAmount>
        => Add(new AdjustDirective(target, AdjustDirection.Increase, percent, Max: max.ToRaw()));

    /// <summary>Increases the target by a fixed amount of the rule set's amount type (Money, Mass…).</summary>
    public void Increase<TAmount>(AdjustTarget target, TAmount amount) where TAmount : IAmount<TAmount>
        => Add(new AdjustDirective(target, AdjustDirection.Increase, null, amount.ToRaw()));

    /// <summary>Increases a plain-number amount (decimal/int/long rule sets).</summary>
    public void Increase(AdjustTarget target, decimal amount) => Add(new AdjustDirective(target, AdjustDirection.Increase, null, RawValue.From(amount)));
    /// <inheritdoc cref="Increase(AdjustTarget, decimal)"/>
    public void Increase(AdjustTarget target, long amount) => Add(new AdjustDirective(target, AdjustDirection.Increase, null, RawValue.From(amount)));
    /// <inheritdoc cref="Increase(AdjustTarget, decimal)"/>
    public void Increase(AdjustTarget target, double amount) => Add(new AdjustDirective(target, AdjustDirection.Increase, null, RawValue.From(amount)));

    /// <summary>Untyped adjustment (tables, dynamic rules): the amount must fit the rule set's amount type.</summary>
    public void Adjust(in AdjustDirective adjustment) => Add(new Directive(adjustment));

    public void RequireApproval(Permission permission, in FieldPath path, RuleCode reason)
        => Add(new ApprovalDirective(permission, Make(path, reason, Severity.Warning, default)));

    public void Add(in Directive directive) => _buffer.Add(in directive, _candidate);

    private readonly Violation Make(in FieldPath path, RuleCode code, Severity severity, in RuleArgs args)
        => new(in path, code, severity, in args, new ViolationOrigin(ViolationSource.Rule, _owner));
}

/// <summary>Pooled scratch list of directives, each tagged with the candidate that issued it.</summary>
internal sealed class DirectiveBuffer
{
    internal struct Entry
    {
        public Directive Directive;
        public int Candidate;
    }

    private Entry[] _items = ArrayPool<Entry>.Shared.Rent(16);

    public int Count { get; private set; }

    public ref Entry this[int index] => ref _items[index];

    public void Add(in Directive directive, int candidate)
    {
        if (Count == _items.Length)
        {
            var bigger = ArrayPool<Entry>.Shared.Rent(_items.Length * 2);
            _items.AsSpan(0, Count).CopyTo(bigger);
            ArrayPool<Entry>.Shared.Return(_items, clearArray: true);
            _items = bigger;
        }
        _items[Count].Directive = directive;
        _items[Count].Candidate = candidate;
        Count++;
    }

    public int CountFor(int candidate)
    {
        int n = 0;
        for (int i = Count - 1; i >= 0 && _items[i].Candidate == candidate; i--) n++;
        return n;
    }

    public void Clear()
    {
        _items.AsSpan(0, Count).Clear();
        Count = 0;
    }

    public void Release()
    {
        Clear();
        ArrayPool<Entry>.Shared.Return(_items);
        _items = [];
    }
}
