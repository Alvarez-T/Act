namespace YFex.Rules;

/// <summary>
/// Running amounts during evaluation (what a <see cref="DerivedField{TSituation,T}"/> may read), typed as the rule set's
/// amount (<c>RunningTotals&lt;Money&gt;</c>, <c>RunningTotals&lt;Mass&gt;</c>, <c>RunningTotals&lt;decimal&gt;</c>…).
/// </summary>
public readonly ref struct RunningTotals<TAmount>
{
    private readonly EngineTotals _totals;

    internal RunningTotals(in EngineTotals totals) => _totals = totals;

    /// <summary>Σ line bases before any adjustment (or the situation base when there are no lines).</summary>
    public TAmount Initial => Amounts.FromDecimal<TAmount>(_totals.Initial, _totals.Type);
    /// <summary>Running amount after every adjustment applied so far.</summary>
    public TAmount Current => Amounts.FromDecimal<TAmount>(_totals.Current, _totals.Type);
    /// <summary>Alias of <see cref="Current"/> — "the total after this stage".</summary>
    public TAmount Final => Current;
    public int LineCount => _totals.LineBases.Length;
    /// <summary>Running base of line <paramref name="index"/> (line adjustments and allocations applied so far).</summary>
    public TAmount LineBase(int index) => Amounts.FromDecimal<TAmount>(_totals.LineBases[index], _totals.Type);
}

/// <summary>Engine-side running totals (decimal storage of the rule set's amount).</summary>
internal readonly ref struct EngineTotals(decimal initial, decimal current, ReadOnlySpan<decimal> lineBases, RawType type)
{
    public decimal Initial { get; } = initial;
    public decimal Current { get; } = current;
    public ReadOnlySpan<decimal> LineBases { get; } = lineBases;
    public RawType Type { get; } = type;
}

/// <summary>Computes a derived field from the situation and the running totals.</summary>
public delegate T DerivedFieldFn<TSituation, TAmount, T>(in TSituation situation, in RunningTotals<TAmount> totals);

/// <summary>
/// Post-stage field computed by the ENGINE from running totals after a stage, so it can depend on adjustments
/// (a <see cref="DerivedAttribute"/> situation property cannot). Can be limited and <see cref="RequestedAttribute"/>-checked
/// like a situation field; tables bind it by name (<c>[Then(DirectiveKind.LimitMin, "InstallmentValue")]</c>).
/// <code>
/// [Requested]
/// public static readonly DerivedField&lt;SaleSituation, Money&gt; InstallmentValue = DerivedField.After(Payment, "InstallmentValue",
///     static (in SaleSituation s, in RunningTotals&lt;Money&gt; t) => s.Installments > 0 ? t.Final / s.Installments : t.Final);
/// </code>
/// </summary>
public sealed class DerivedField<TSituation, T> : DerivedField<TSituation> where TSituation : ISituation<TSituation>
{
    private readonly ComputeFn _compute;

    internal delegate T ComputeFn(in TSituation situation, in EngineTotals totals);

    internal DerivedField(Stage after, string name, Type amountType, ComputeFn compute) : base(after, name, amountType)
        => _compute = compute;

    internal override RawValue ComputeRaw(in TSituation situation, in EngineTotals totals) => RawValueConvert.From(_compute(in situation, in totals));

    /// <summary>Typed-limit convenience (<c>d.Limit(SaleRules.InstallmentValue, min: …)</c>).</summary>
    public static implicit operator SituationFieldRef(DerivedField<TSituation, T> f) => f.Ref;
}

/// <summary>Situation-typed part of <see cref="DerivedField{TSituation,T}"/> (the engine computes it without knowing T).</summary>
public abstract class DerivedField<TSituation>(Stage after, string name, Type amountType) : DerivedField(after, name, amountType)
    where TSituation : ISituation<TSituation>
{
    internal abstract RawValue ComputeRaw(in TSituation situation, in EngineTotals totals);
}

/// <summary>Non-generic part of <see cref="DerivedField{TSituation,T}"/>; factory <see cref="After{TSituation,TAmount,T}"/>.</summary>
public abstract class DerivedField(Stage after, string name, Type amountType)
{
    /// <summary>Computed after this stage's last group.</summary>
    public Stage Stage { get; } = after ?? throw new ArgumentNullException(nameof(after));
    public string Name { get; } = name;
    /// <summary>The <c>TAmount</c> of the totals it reads (must be the rule set's amount type).</summary>
    public Type AmountType { get; } = amountType;
    /// <summary>Assigned when the rule set definition is built: <see cref="SituationFieldRef.DerivedBase"/> + index.</summary>
    public ushort Id { get; private set; }
    public bool IsRequested { get; private set; }
    public SituationFieldRef Ref => new(Id, Name);

    internal void Register(ushort id, bool requested)
    {
        if (Id != 0 && Id != id) throw new InvalidOperationException($"Derived field '{Name}' belongs to another rule set.");
        Id = id;
        IsRequested = requested;
    }

    /// <summary>Declares a field computed after <paramref name="stage"/> from the running totals of amount type <typeparamref name="TAmount"/>.</summary>
    public static DerivedField<TSituation, T> After<TSituation, TAmount, T>(Stage stage, string name, DerivedFieldFn<TSituation, TAmount, T> compute)
        where TSituation : ISituation<TSituation>
    {
        ArgumentNullException.ThrowIfNull(compute);
        return new(stage, name, typeof(TAmount), (in TSituation s, in EngineTotals t) => compute(in s, new RunningTotals<TAmount>(in t)));
    }
}
