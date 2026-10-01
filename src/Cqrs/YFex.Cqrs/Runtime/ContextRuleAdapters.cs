using YFex.Cqrs.Configuration;
using YFex.Validation;

namespace YFex.Cqrs.Runtime;

// Adapters from the configuration API shapes to IContextRule<T> (stage 5).

/// <summary><c>.Validate((msg, ct) =&gt; ValueTask&lt;ValidationResult&gt;)</c>.</summary>
internal sealed class InlineResultRule<T>(Func<T, CancellationToken, ValueTask<ValidationResult>> validate) : IContextRule<T>
{
    public async ValueTask CheckAsync(T value, ViolationCollector violations, CancellationToken ct)
        => violations.AddRange(await validate(value, ct).ConfigureAwait(false));
}

/// <summary><c>.Validate((msg, violations, ct) =&gt; …)</c>.</summary>
internal sealed class InlineCollectorRule<T>(Func<T, ViolationCollector, CancellationToken, ValueTask> rule) : IContextRule<T>
{
    public ValueTask CheckAsync(T value, ViolationCollector violations, CancellationToken ct) => rule(value, violations, ct);
}

#pragma warning disable CS0618 // adapting the obsolete validator interfaces is the point
/// <summary>Obsolete <see cref="ICommandValidator{TCommand}"/> / <see cref="IQueryValidator{TQuery}"/> as a context rule.</summary>
internal sealed class LegacyValidatorRule<T> : IContextRule<T>
{
    private readonly Func<T, CancellationToken, ValueTask<ValidationResult>> _validate;

    public LegacyValidatorRule(object validator) => _validate = validator switch
    {
        ICommandValidator<T> c => c.ValidateAsync,
        IQueryValidator<T> q => q.ValidateAsync,
        _ => throw new ArgumentException($"{validator.GetType().Name} is not a validator of {typeof(T).Name}.", nameof(validator)),
    };

    public async ValueTask CheckAsync(T value, ViolationCollector violations, CancellationToken ct)
        => violations.AddRange(await _validate(value, ct).ConfigureAwait(false));
}
#pragma warning restore CS0618
