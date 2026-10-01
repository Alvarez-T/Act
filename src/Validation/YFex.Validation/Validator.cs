using System.Runtime.CompilerServices;

namespace YFex.Validation;

/// <summary>
/// Proof that a value passed shape validation (attributes / fluent / invariants). Only YFex.Validation
/// creates it (via <see cref="Validator.Check{T}"/>), so a handler that requires <c>Valid&lt;T&gt;</c>
/// cannot receive unvalidated input. Warnings/infos found during the check travel along.
/// </summary>
public readonly struct Valid<T>
{
    internal Valid(T value, ViolationList warnings)
    {
        Value = value;
        Warnings = warnings;
    }

    public T Value { get; }

    /// <summary>Non-blocking violations (Warning/Info) found by the check.</summary>
    public ViolationList Warnings { get; }
}

/// <summary>Case of <see cref="Validated{T}"/>: the value failed validation.</summary>
public readonly record struct Invalid(ValidationResult Result);

/// <summary>Which case a <see cref="Validated{T}"/> holds.</summary>
public enum ValidatedKind : byte { None = 0, Valid = 1, Invalid = 2 }

/// <summary>Parse-don't-validate result: <see cref="Valid{T}"/> | <see cref="Invalid"/>.</summary>
[Union]
public readonly struct Validated<T> : IUnion
{
    private readonly Valid<T> _valid;
    private readonly ValidationResult _invalid;
    private readonly byte _tag; // 0 = none, 1 = valid, 2 = invalid

    public Validated(Valid<T> valid)
    {
        _valid = valid;
        _invalid = default;
        _tag = 1;
    }

    public Validated(Invalid invalid)
    {
        _valid = default;
        _invalid = invalid.Result;
        _tag = 2;
    }

    /// <summary>Required by <see cref="IUnion"/>. <b>Boxes</b> — never call it in library code.</summary>
    public object? Value => _tag switch
    {
        1 => _valid,
        2 => new Invalid(_invalid),
        _ => null,
    };

    public bool HasValue => _tag != 0;
    public ValidatedKind Kind => (ValidatedKind)_tag;
    public bool IsValid => _tag == 1;

    public bool TryGetValue(out Valid<T> value)
    {
        value = _valid;
        return _tag == 1;
    }

    public bool TryGetValue(out Invalid value)
    {
        value = new Invalid(_invalid);
        return _tag == 2;
    }

    public static implicit operator Validated<T>(Valid<T> valid) => new(valid);
    public static implicit operator Validated<T>(Invalid invalid) => new(invalid);
}

/// <summary>
/// Entry points over generated <see cref="IValidatable{TSelf}"/> types. Zero allocation on success.
/// <code>
/// var v = Validator.Check(cmd);
/// if (v.TryGetValue(out Invalid bad)) return bad.Result;
/// </code>
/// </summary>
public static class Validator
{
    public static Validated<T> Check<T>(in T value) where T : IValidatable<T>
    {
        using var scope = ViolationWriter.Begin(out var w);
        T.Validate(in value, ref w);
        if (w.HasErrors) return new Invalid(w.ToResult());
        return new Valid<T>(value, w.Count == 0 ? default : w.ToResult().Violations);
    }

    public static ValidationResult Validate<T>(in T value) where T : IValidatable<T>
    {
        using var scope = ViolationWriter.Begin(out var w);
        T.Validate(in value, ref w);
        return w.ToResult();
    }

    public static bool IsValid<T>(in T value) where T : IValidatable<T>
    {
        using var scope = ViolationWriter.Begin(out var w);
        return T.Validate(in value, ref w);
    }

    /// <summary>
    /// Validates a nested value / collection element and re-targets its violations under
    /// <paramref name="prefix"/> (<c>"City"</c> under <c>Addresses[2]</c> → <c>Addresses[2].City</c>).
    /// Used by generated <c>ValidateAt</c>. No allocation when the value is valid; on failure the
    /// combined path templates are allocated (edge only).
    /// </summary>
    public static bool ValidateAt<T>(in T value, ref ViolationWriter w, in FieldPath prefix) where T : IValidatable<T>
    {
        using var scope = ViolationWriter.Begin(out var inner);
        inner.CurrentOrigin = w.CurrentOrigin;
        bool ok = T.Validate(in value, ref inner);
        foreach (ref readonly Violation v in inner.AsSpan())
            w.Add(v.WithPath(v.Path.Prefixed(in prefix)));
        return ok;
    }

    /// <summary>
    /// Wraps a value that was validated elsewhere (e.g. by the Cqrs pipeline through
    /// <see cref="ValidationSlot{T}"/>). Only for infrastructure that has just run the checks.
    /// </summary>
    public static Valid<T> AssumeValid<T>(T value, ViolationList warnings = default) => new(value, warnings);
}

/// <summary>
/// Static generic holder of a type's generated <c>Validate</c>, filled by a generated
/// <c>[ModuleInitializer]</c> in every assembly with validatable types. A function pointer: no
/// delegate allocation, no boxing, no casts — generic dispatchers call
/// <c>ValidationSlot&lt;TCommand&gt;.Validate(in cmd, ref w)</c> without any policy lookup.
/// <code>ValidationSlot&lt;CreateCustomer&gt;.Set(&amp;CreateCustomer.Validate);</code>
/// </summary>
public static unsafe class ValidationSlot<T>
{
    // unsafe: function pointer avoids a delegate allocation and interface dispatch (README #4).
    private static delegate*<in T, ref ViolationWriter, bool> s_validate;

    public static bool IsSet => s_validate != null;

    public static void Set(delegate*<in T, ref ViolationWriter, bool> validate) => s_validate = validate;

    /// <summary>
    /// Runs the generated validation. Without one (message not <c>partial</c>, or only runtime fluent rules), runs
    /// <see cref="RuntimeRules{T}"/> instead — true (valid) when neither added an error.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Validate(in T value, ref ViolationWriter w)
        => s_validate != null ? s_validate(in value, ref w) : RunRuntime(in value, ref w);

    private static bool RunRuntime(in T value, ref ViolationWriter w)
    {
        if (!RuntimeRules<T>.HasRules) return true;
        int errors = w.ErrorCount;
        RuntimeRules<T>.Run(in value, ref w);
        return w.ErrorCount == errors;
    }
}

/// <summary>Registration helpers used by generated module initializers.</summary>
public static class ValidationSlots
{
    /// <summary>
    /// Stores <typeparamref name="T"/>'s generated <c>Validate</c> in <see cref="ValidationSlot{T}"/>.
    /// The function pointer is taken here, so consumer projects need no <c>AllowUnsafeBlocks</c>.
    /// </summary>
    public static unsafe void Register<T>() where T : IValidatable<T>
    {
        ValidationSlot<T>.Set(&T.Validate);
        CoercionSlot<T>.Set(&T.Coerce);
        ObjectValidation.Register<T>();
        JsonSchemas.Register<T>();
    }
}

/// <summary>
/// Static generic holder of a type's generated <c>Coerce</c> (<c>Range(coerce: true)</c> clamping), filled next to
/// <see cref="ValidationSlot{T}"/>. Dispatchers coerce queries BEFORE the cache key is computed (09 §7).
/// </summary>
public static unsafe class CoercionSlot<T>
{
    // unsafe: function pointer, same reasoning as ValidationSlot<T> (README #4).
    private static delegate*<in T, T> s_coerce;

    public static bool IsSet => s_coerce != null;

    public static void Set(delegate*<in T, T> coerce) => s_coerce = coerce;

    /// <summary>The coerced value (the value itself when the type has no coercion).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T Apply(in T value) => s_coerce == null ? value : s_coerce(in value);
}

/// <summary>Stage-1 validation of a message known only as <see cref="object"/>.</summary>
public delegate bool ObjectValidateFn(object value, ref ViolationWriter w);

/// <summary>
/// Type-keyed shape validation for type-erased boundaries (the server <c>IHandlerInvoker</c> decorator, outbox
/// replay) where the static message type is unknown. Filled by <see cref="ValidationSlots.Register{T}"/> (generated
/// module initializers) and by <see cref="ValidationBuilder{T}"/> (runtime-only fluent messages); each entry forwards
/// to <see cref="ValidationSlot{T}.Validate"/>. Generic dispatchers use <see cref="ValidationSlot{T}"/> directly.
/// </summary>
public static class ObjectValidation
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ObjectValidateFn> s_byType = new();

    public static void Register<T>()
    {
        s_byType.TryAdd(typeof(T), Thunk<T>.Validate);
        if (CoercionSlot<T>.IsSet) s_coerceByType.TryAdd(typeof(T), Thunk<T>.Coerce);
    }

    public static bool IsRegistered(Type type) => s_byType.ContainsKey(type);

    /// <summary>Validates <paramref name="value"/> with its runtime type's rules; true when it has none.</summary>
    public static bool Validate(object value, ref ViolationWriter w)
    {
        ArgumentNullException.ThrowIfNull(value);
        return !s_byType.TryGetValue(value.GetType(), out var validate) || validate(value, ref w);
    }

    /// <summary>Coerces <paramref name="value"/> with its runtime type's <see cref="CoercionSlot{T}"/> (same instance when none).</summary>
    public static object Coerce(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return s_coerceByType.TryGetValue(value.GetType(), out var coerce) ? coerce(value) : value;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, object>> s_coerceByType = new();

    private static class Thunk<T>
    {
        public static readonly ObjectValidateFn Validate = static (object value, ref ViolationWriter w) =>
        {
            T typed = (T)value;
            return ValidationSlot<T>.Validate(in typed, ref w);
        };

        public static readonly Func<object, object> Coerce = static value =>
        {
            T typed = (T)value;
            return CoercionSlot<T>.Apply(in typed)!;
        };
    }
}
