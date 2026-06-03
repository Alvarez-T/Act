namespace YFex.Cqrs;

public sealed record ValidationError(string PropertyName, string Message, string? ErrorCode = null);

public readonly struct ValidationResult
{
    public bool IsValid { get; }
    public IReadOnlyList<ValidationError> Errors { get; }

    public ValidationResult(bool isValid, IReadOnlyList<ValidationError>? errors = null)
    {
        IsValid = isValid;
        Errors = errors ?? [];
    }

    public static ValidationResult Success() => new(true);
    public static ValidationResult Failure(params ValidationError[] errors) => new(false, errors);
    public static ValidationResult Failure(string propertyName, string message) =>
        new(false, [new ValidationError(propertyName, message)]);
}
