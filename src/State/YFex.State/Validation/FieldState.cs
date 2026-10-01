using System.Runtime.CompilerServices;
using YFex.Validation;

namespace YFex.State.Validation;

/// <summary>
/// Rich per-field state: Pristine | Validating | Valid | Invalid(violations) | Warned(violations).
/// The neutral <see cref="FieldStatus"/> enum (YFex.Validation) is what UI adapters use; this union is for
/// code that wants the violations with the case.
/// <code>
/// switch (vm.Validation.GetState("Email"))
/// {
///     case FieldState.InvalidCase i: Show(i.Violations); break;
///     case FieldState.ValidatingCase: ShowSpinner(); break;
/// }
/// </code>
/// </summary>
[Union]
public readonly struct FieldState : IUnion
{
    public readonly record struct PristineCase;
    public readonly record struct ValidatingCase;
    public readonly record struct ValidCase;
    public readonly record struct InvalidCase(ViolationList Violations);
    public readonly record struct WarnedCase(ViolationList Violations);

    private readonly ViolationList _violations;
    private readonly FieldStatus _status;

    private FieldState(FieldStatus status, ViolationList violations)
    {
        _status = status;
        _violations = violations;
    }

    public FieldState(InvalidCase invalid) : this(FieldStatus.Invalid, invalid.Violations) { }
    public FieldState(WarnedCase warned) : this(FieldStatus.Warned, warned.Violations) { }

    public static FieldState Pristine => default;
    public static FieldState Validating => new(FieldStatus.Validating, default);
    public static FieldState Valid => new(FieldStatus.Valid, default);

    /// <summary>Cheap switch without pattern matching.</summary>
    public FieldStatus Kind => _status;

    /// <summary>Violations of the Invalid / Warned cases (empty otherwise).</summary>
    public ViolationList Violations => _violations;

    /// <summary>Required by <see cref="IUnion"/>. <b>Boxes</b> — never call it in library code.</summary>
    public object? Value => _status switch
    {
        FieldStatus.Validating => new ValidatingCase(),
        FieldStatus.Valid => new ValidCase(),
        FieldStatus.Invalid => new InvalidCase(_violations),
        FieldStatus.Warned => new WarnedCase(_violations),
        _ => new PristineCase(),
    };

    public bool HasValue => true;

    public bool TryGetValue(out PristineCase value) { value = default; return _status == FieldStatus.Pristine; }
    public bool TryGetValue(out ValidatingCase value) { value = default; return _status == FieldStatus.Validating; }
    public bool TryGetValue(out ValidCase value) { value = default; return _status == FieldStatus.Valid; }

    public bool TryGetValue(out InvalidCase value)
    {
        value = new InvalidCase(_violations);
        return _status == FieldStatus.Invalid;
    }

    public bool TryGetValue(out WarnedCase value)
    {
        value = new WarnedCase(_violations);
        return _status == FieldStatus.Warned;
    }
}

/// <summary>
/// Handle of one async check (<see cref="ValidationBag.BeginAsync"/>). Its result is applied only while it is
/// still the latest ticket for its path; <see cref="Token"/> is cancelled when a newer value arrives.
/// </summary>
public readonly struct AsyncValidationTicket
{
    internal AsyncValidationTicket(string path, int version, CancellationToken token)
    {
        Path = path;
        Version = version;
        Token = token;
    }

    public string Path { get; }
    public int Version { get; }
    public CancellationToken Token { get; }
}
