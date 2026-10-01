namespace YFex.Validation;

/// <summary>Field state for spinners/icons.</summary>
public enum FieldStatus : byte
{
    Pristine = 0,
    Validating = 1,
    Valid = 2,
    Invalid = 3,
    Warned = 4,
}

/// <summary>
/// What a UI control needs from its DataContext — framework-neutral (Avalonia/Uno/Blazor adapters
/// consume it). Implemented by generated VM code. Paths are the VM's own property paths
/// (<c>"Name"</c>, <c>"Lines[2].Quantity"</c>).
/// </summary>
public interface IValidationSource
{
    /// <summary>Static (attributes/fluent/borrowed) merged with dynamic (current decision) constraints.</summary>
    bool TryGetConstraints(ReadOnlySpan<char> path, out FieldConstraints constraints);

    /// <summary>Violations currently VISIBLE for <paramref name="path"/> (display policy applied: touched/submitted).</summary>
    ViolationList GetVisibleViolations(ReadOnlySpan<char> path);

    /// <summary>Field state: Pristine | Validating | Valid | Invalid | Warned.</summary>
    FieldStatus GetStatus(ReadOnlySpan<char> path);

    /// <summary>Controls call this on blur/commit so "show after touched" works.</summary>
    void Touch(ReadOnlySpan<char> path);

    /// <summary>Raised with the VM path when violations/status change (<c>""</c> = everything / form level).</summary>
    event Action<string>? ViolationsChanged;

    /// <summary>Raised when constraints change (a new decision arrived) so controls re-read Min/Max/Options.</summary>
    event Action<string>? ConstraintsChanged;
}
