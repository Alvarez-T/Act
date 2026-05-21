using System;

namespace YFex.State.Validation;

/// <summary>
/// Immutable validation outcome. Success is <c>default</c> — zero allocation on the happy path.
/// </summary>
public readonly struct ValidationResult : IEquatable<ValidationResult>
{
    public string? Message { get; }
    public string PropertyName { get; }
    public ValidationSeverity Severity { get; }

    public bool IsSuccess => Message is null;

    public static readonly ValidationResult Success = default;

    public ValidationResult(string message, string propertyName,
        ValidationSeverity severity = ValidationSeverity.Error)
    {
        Message = message;
        PropertyName = propertyName;
        Severity = severity;
    }

    public bool Equals(ValidationResult other) =>
        PropertyName == other.PropertyName &&
        Severity == other.Severity &&
        string.Equals(Message, other.Message, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ValidationResult r && Equals(r);
    public override int GetHashCode() => HashCode.Combine(Message, PropertyName, Severity);
    public static bool operator ==(ValidationResult left, ValidationResult right) => left.Equals(right);
    public static bool operator !=(ValidationResult left, ValidationResult right) => !left.Equals(right);

    public override string ToString() => IsSuccess ? "Success" : $"{Severity}: {Message} [{PropertyName}]";
}
