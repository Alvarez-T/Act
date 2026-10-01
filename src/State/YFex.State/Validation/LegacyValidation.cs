using System.ComponentModel;
using YFex.Validation;

namespace YFex.State.Validation;

#pragma warning disable CS0618 // this adapter exists to keep the obsolete validator contracts working

/// <summary>
/// Adapts the obsolete <c>[ValidateWith(typeof)]</c> / <c>[ValidateAsync(typeof)]</c> validators
/// (<see cref="IValidator{T}"/>, <see cref="IAsyncValidator{T}"/>) to violations. Called by generated code only.
/// The legacy message travels in <see cref="Violation.Detail"/> (bypasses the catalog).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LegacyValidation
{
    /// <summary>Code of every violation produced by a legacy validator.</summary>
    public const string Code = "state.legacy";

    /// <summary>Adds the legacy failure (if any); returns false when it failed (the chain stops, as before).</summary>
    public static bool Add(in ValidationResult result, ref ViolationWriter w, in FieldPath path)
    {
        if (result.IsSuccess) return true;
        w.Add(ToViolation(in result, in path));
        return false;
    }

    public static Violation ToViolation(in ValidationResult result, in FieldPath path)
        => new(in path, Code, (Severity)(byte)result.Severity, default, default, result.Message);
}
