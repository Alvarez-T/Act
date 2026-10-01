using FluentValidation;
using YFex.Cqrs;
using YFex.Cqrs.Configuration;
using YFex.Validation;
using FluentResult = FluentValidation.Results.ValidationResult;

namespace YFex.Cqrs.FluentValidation;

/// <summary>
/// <c>.UseFluentValidator(new TValidator())</c> on the CQRS configuration builders. The validator runs as a context rule (stage 5); each failure becomes a <see cref="Violation"/>:
/// path = <c>PropertyName</c>, code = <c>ErrorCode</c> (or <c>"fluent"</c>), detail = <c>ErrorMessage</c>,
/// severity mapped from FluentValidation's.
/// </summary>
public static class FluentValidationExtensions
{
    // Classic extensions taking the validator instance: `.UseFluentValidator(new LeadValidator())`. (The former
    // `.UseFluentValidator<TValidator>()` shape needs explicit type arguments on a C# 14 generic extension-block member,
    // which the .NET 11 preview compiler does not resolve yet — see validation doc 13, Phase 3 step 7.)

    public static CommandConfigurationBuilder<TCommand> UseFluentValidator<TCommand>(
        this CommandConfigurationBuilder<TCommand> builder, IValidator<TCommand> validator)
        where TCommand : ICommand
    {
        ArgumentNullException.ThrowIfNull(validator);
        return builder.Validate((command, ct) => ValidateAsync(validator, command, ct));
    }

    public static CommandConfigurationBuilder<TCommand, TResult> UseFluentValidator<TCommand, TResult>(
        this CommandConfigurationBuilder<TCommand, TResult> builder, IValidator<TCommand> validator)
        where TCommand : ICommand<TResult>
    {
        ArgumentNullException.ThrowIfNull(validator);
        return builder.Validate((command, ct) => ValidateAsync(validator, command, ct));
    }

    public static QueryConfigurationBuilder<TQuery, TResult> UseFluentValidator<TQuery, TResult>(
        this QueryConfigurationBuilder<TQuery, TResult> builder, IValidator<TQuery> validator)
        where TQuery : IQuery<TResult>
    {
        ArgumentNullException.ThrowIfNull(validator);
        return builder.Validate((query, ct) => ValidateAsync(validator, query, ct));
    }

    private static async ValueTask<ValidationResult> ValidateAsync<T>(IValidator<T> validator, T value, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(value, ct).ConfigureAwait(false);
        return result.IsValid ? ValidationResult.Valid : ToResult(result);
    }

    /// <summary>FluentValidation failures → violations (public for apps that call validators directly).</summary>
    public static ValidationResult ToResult(FluentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var failures = result.Errors;
        if (failures.Count == 0) return ValidationResult.Valid;
        var violations = new Violation[failures.Count];
        for (int i = 0; i < violations.Length; i++)
        {
            var f = failures[i];
            var severity = f.Severity switch
            {
                global::FluentValidation.Severity.Warning => YFex.Validation.Severity.Warning,
                global::FluentValidation.Severity.Info => YFex.Validation.Severity.Info,
                _ => YFex.Validation.Severity.Error,
            };
            var path = string.IsNullOrEmpty(f.PropertyName) ? FieldPath.Empty : FieldPath.Of(f.PropertyName);
            violations[i] = new Violation(in path, string.IsNullOrEmpty(f.ErrorCode) ? "fluent" : f.ErrorCode, severity,
                detail: f.ErrorMessage);
        }
        return ValidationResult.Of(ViolationList.FromOwnedArray(violations));
    }
}
