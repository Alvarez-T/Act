namespace YFex.Validation;

/// <summary>Which members of a <see cref="FieldConstraints"/> are set.</summary>
[Flags]
public enum ConstraintFlags : ushort
{
    None = 0,
    Required = 1,
    MinLength = 2,
    MaxLength = 4,
    Min = 8,
    Max = 16,
    Step = 32,
    Options = 64,
    Format = 128,
}

/// <summary>
/// Machine-readable limits of ONE field, projected by the UI onto controls. Static constraints come
/// from attributes/fluent config (generated constants); dynamic ones from a YFex.Rules decision.
/// <see cref="Merge"/> = most restrictive.
/// <code>
/// [Required, Length(2, 80)] string Name → Required, MinLength = 2, MaxLength = 80
/// Decision "Installments ≤ 6"          → Max = RawValue.From(6), Options = {1..6}
/// </code>
/// </summary>
public readonly struct FieldConstraints : IEquatable<FieldConstraints>
{
    private FieldConstraints(ConstraintFlags flags, int minLength, int maxLength, RawValue min, RawValue max,
        RawValue step, OptionSet options, RuleCode format, Severity maxSeverity)
    {
        Flags = flags;
        MinLength = minLength;
        MaxLength = maxLength;
        Min = min;
        Max = max;
        Step = step;
        Options = options;
        Format = format;
        MaxSeverity = maxSeverity;
    }

    public ConstraintFlags Flags { get; }
    public bool Required => (Flags & ConstraintFlags.Required) != 0;

    /// <summary>Valid when <see cref="ConstraintFlags.MinLength"/> is set.</summary>
    public int MinLength { get; }
    /// <summary>Valid when <see cref="ConstraintFlags.MaxLength"/> is set.</summary>
    public int MaxLength { get; }
    /// <summary>Inclusive numeric/date/money lower bound (<see cref="RawValue.None"/> when unset).</summary>
    public RawValue Min { get; }
    /// <summary>Inclusive upper bound.</summary>
    public RawValue Max { get; }
    /// <summary>Increment hint for steppers.</summary>
    public RawValue Step { get; }
    /// <summary>Allowed discrete values (Grant directive / enum subset). Empty = unrestricted.</summary>
    public OptionSet Options { get; }
    /// <summary>Mask/format hint: <c>"br.document"</c>, <c>"email"</c>, <c>"phone"</c>.</summary>
    public RuleCode Format { get; }
    /// <summary>Strongest severity among the field's rules (UI shows the "required" marker only for Error).</summary>
    public Severity MaxSeverity { get; }

    public bool Has(ConstraintFlags flag) => (Flags & flag) == flag;

    public static FieldConstraints None => default;

    /// <summary>Factory used by generated code; only the arguments passed are flagged as set.</summary>
    public static FieldConstraints Create(bool required = false, int? minLength = null, int? maxLength = null,
        RawValue min = default, RawValue max = default, RawValue step = default,
        OptionSet options = default, RuleCode format = default, Severity maxSeverity = Severity.Error)
    {
        ConstraintFlags f = ConstraintFlags.None;
        if (required) f |= ConstraintFlags.Required;
        if (minLength.HasValue) f |= ConstraintFlags.MinLength;
        if (maxLength.HasValue) f |= ConstraintFlags.MaxLength;
        if (!min.IsNone) f |= ConstraintFlags.Min;
        if (!max.IsNone) f |= ConstraintFlags.Max;
        if (!step.IsNone) f |= ConstraintFlags.Step;
        if (!options.IsEmpty) f |= ConstraintFlags.Options;
        if (!format.IsEmpty) f |= ConstraintFlags.Format;
        return new(f, minLength ?? 0, maxLength ?? 0, min, max, step, options, format, maxSeverity);
    }

    /// <summary>
    /// Most restrictive combination: required if either is; max of mins; min of maxes; intersection
    /// of options; this side's step/format win when both are set; strongest severity. Bounds of
    /// incomparable kinds keep this side's value.
    /// </summary>
    public FieldConstraints Merge(in FieldConstraints other)
    {
        if (other.Flags == ConstraintFlags.None) return this;
        if (Flags == ConstraintFlags.None) return other;

        ConstraintFlags f = Flags | other.Flags;

        int minLength = Has(ConstraintFlags.MinLength) && other.Has(ConstraintFlags.MinLength)
            ? Math.Max(MinLength, other.MinLength)
            : Has(ConstraintFlags.MinLength) ? MinLength : other.MinLength;
        int maxLength = Has(ConstraintFlags.MaxLength) && other.Has(ConstraintFlags.MaxLength)
            ? Math.Min(MaxLength, other.MaxLength)
            : Has(ConstraintFlags.MaxLength) ? MaxLength : other.MaxLength;

        RawValue min = Pick(Min, other.Min, preferGreater: true);
        RawValue max = Pick(Max, other.Max, preferGreater: false);

        return new(f, minLength, maxLength, min, max,
            Step.IsNone ? other.Step : Step,
            Options.Intersect(other.Options),
            Format.IsEmpty ? other.Format : Format,
            (Severity)Math.Max((byte)MaxSeverity, (byte)other.MaxSeverity));
    }

    private static RawValue Pick(in RawValue a, in RawValue b, bool preferGreater)
    {
        if (a.IsNone) return b;
        if (b.IsNone) return a;
        if (!a.TryCompareTo(in b, out int c)) return a;
        return preferGreater ? (c >= 0 ? a : b) : (c <= 0 ? a : b);
    }

    public bool Equals(FieldConstraints other)
        => Flags == other.Flags && MinLength == other.MinLength && MaxLength == other.MaxLength
           && Min.Equals(other.Min) && Max.Equals(other.Max) && Step.Equals(other.Step)
           && Options.Equals(other.Options) && Format == other.Format && MaxSeverity == other.MaxSeverity;

    public override bool Equals(object? obj) => obj is FieldConstraints other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(Flags, MinLength, MaxLength, Min, Max, Options, Format, MaxSeverity);

    public static bool operator ==(FieldConstraints left, FieldConstraints right) => left.Equals(right);
    public static bool operator !=(FieldConstraints left, FieldConstraints right) => !left.Equals(right);
}
