namespace YFex.State.Validation;

/// <summary>Legacy severity — identical values to <see cref="YFex.Validation.Severity"/>.</summary>
[System.Obsolete("Use YFex.Validation.Severity (same values).")]
public enum ValidationSeverity : byte
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
