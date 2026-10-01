namespace YFex.Rules;

/// <summary>
/// A named, typed, versioned value a code rule reads through <see cref="RuleContext.Get{T}"/> (04 §3.2, 05 §5).
/// Code decides the logic, the data owner decides the number. Rule tiers named in <see cref="RuleParameter.EditableAt"/>
/// may publish overrides; <see cref="Guard"/> is a sealed bound — overrides outside it are rejected at publish and
/// clamped + traced at evaluation.
/// <code>
/// public static readonly RuleParameter&lt;Percentual&gt; DiscountRate =
///     new("rate", Percentual.FromPercentage(5), editableAt: [nameof(Sale.Tenant)], guard: new(Percentual.Zero, Percentual.FromPercentage(15)));
/// </code>
/// </summary>
/// <remarks>
/// Runtime publishing (4b) needs a JSON codec: scalars supported by <see cref="RawValueConvert"/> get one automatically;
/// enums use <see cref="ParameterCodec.Enum{TEnum}"/>; other types (e.g. a product set) pass their own
/// <see cref="ParameterCodec{T}"/> — without one the parameter can't be published at runtime.
/// </remarks>
public sealed class RuleParameter<T>(string name, T @default, string[]? editableAt = null, Bounds<T>? guard = null,
    ParameterCodec<T>? codec = null)
    : RuleParameter(name, editableAt)
{
    private readonly ParameterCodec<T>? _codec = codec ?? (RawValueConvert.IsSupported<T>() ? ParameterCodec.Scalar<T>() : null);

    public T Default { get; } = @default;
    public Bounds<T>? Guard { get; } = guard;
    public override bool CanPublish => _codec is not null;
    public override Type ValueType => typeof(T);

    /// <summary>
    /// <paramref name="value"/> clamped into <see cref="Guard"/> (no guard or non-comparable T → unchanged).
    /// <paramref name="clamped"/> tells whether it moved.
    /// </summary>
    public T Clamp(T value, out bool clamped)
    {
        clamped = false;
        if (Guard is not { } g) return value;
        var cmp = Comparer<T>.Default;
        if (cmp.Compare(value, g.Min) < 0) { clamped = true; return g.Min; }
        if (cmp.Compare(value, g.Max) > 0) { clamped = true; return g.Max; }
        return value;
    }

    /// <summary>True when <paramref name="value"/> respects <see cref="Guard"/> (publish-time check).</summary>
    public bool IsWithinGuard(T value)
    {
        Clamp(value, out bool clamped);
        return !clamped;
    }

    internal override ParameterValue CreateValue(object? value, int tier, long version, out bool clamped)
    {
        if (value is not T typed) throw new ArgumentException($"Parameter '{OwnerCode}.{Name}' expects {typeof(T).Name}.", nameof(value));
        return new ParameterValue<T>(Clamp(typed, out clamped), tier, version);
    }

    public override object? ReadJson(ref System.Text.Json.Utf8JsonReader reader)
        => (_codec ?? throw new NotSupportedException($"Parameter '{Key}' has no JSON codec.")).Read(ref reader);

    public override void WriteJson(System.Text.Json.Utf8JsonWriter writer, object? value)
        => (_codec ?? throw new NotSupportedException($"Parameter '{Key}' has no JSON codec.")).Write(writer, value is T t ? t : Default);

    public override bool IsWithinGuard(object? value) => value is T t && IsWithinGuard(t);
    public override object? DefaultValue => Default;
}

/// <summary>JSON codec of a parameter value (publish, store, editor).</summary>
public sealed record ParameterCodec<T>(JsonWriteFn<T> Write, JsonReadFn<T> Read);

/// <summary>Codec factories.</summary>
public static class ParameterCodec
{
    public static ParameterCodec<T> Scalar<T>() => new(JsonScalar.Write, JsonScalar.Read<T>);
    public static ParameterCodec<TEnum> Enum<TEnum>() where TEnum : struct, System.Enum => new(EnumJson<TEnum>.Write, EnumJson<TEnum>.Read);
}

/// <summary>Non-generic part of <see cref="RuleParameter{T}"/> (catalog, publish pipeline).</summary>
public abstract class RuleParameter(string name, string[]? editableAt)
{
    private readonly string[] _editableAt = editableAt ?? [];

    public string Name { get; } = name;

    /// <summary>Names of the situation's <see cref="RuleTierAttribute"/> fields whose tiers may publish values (empty = code only).</summary>
    public ReadOnlySpan<string> EditableAt => _editableAt;

    /// <summary>True when some tier may publish values.</summary>
    public bool IsEditable => _editableAt.Length > 0;

    /// <summary>Code of the rule declaring it (set once when the rule set definition is built).</summary>
    public string? OwnerCode { get; private set; }

    /// <summary>Index into a compiled rule set's parameter values (−1 until registered).</summary>
    public int Slot { get; private set; } = -1;

    /// <summary><c>"loyalty.gold.rate"</c> — the key overrides are published under.</summary>
    public string Key => OwnerCode is null ? Name : OwnerCode + "." + Name;

    /// <summary>True when the value has a JSON codec (runtime publishing possible).</summary>
    public abstract bool CanPublish { get; }
    public abstract Type ValueType { get; }
    public abstract object? DefaultValue { get; }
    /// <summary>Reads a published value (boxes — publish/load time only).</summary>
    public abstract object? ReadJson(ref System.Text.Json.Utf8JsonReader reader);
    public abstract void WriteJson(System.Text.Json.Utf8JsonWriter writer, object? value);
    /// <summary>Publish-time guard check (reject instead of clamp).</summary>
    public abstract bool IsWithinGuard(object? value);

    internal void Register(string ownerCode, int slot)
    {
        if (Slot >= 0 && (Slot != slot || OwnerCode != ownerCode))
            throw new InvalidOperationException($"Parameter '{Name}' is already registered as '{Key}' (slot {Slot}); a parameter belongs to one rule.");
        OwnerCode = ownerCode;
        Slot = slot;
    }

    /// <summary>Boxes only at publish/compile time (never during evaluation).</summary>
    internal abstract ParameterValue CreateValue(object? value, int tier, long version, out bool clamped);
}

/// <summary>Resolved value of a parameter inside a compiled rule set.</summary>
public abstract class ParameterValue(int tier, long version)
{
    /// <summary>Depth of the tier path the value was published at (0 = code).</summary>
    public int Tier { get; } = tier;
    public long Version { get; } = version;
}

/// <summary>Typed holder read by <see cref="RuleContext.Get{T}"/> without boxing.</summary>
public sealed class ParameterValue<T>(T value, int tier, long version) : ParameterValue(tier, version)
{
    public T Value { get; } = value;
}
