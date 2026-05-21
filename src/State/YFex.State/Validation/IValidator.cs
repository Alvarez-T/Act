namespace YFex.State.Validation;

/// <summary>
/// Synchronous validator. Static abstract member allows the generator to emit a direct devirtualized call:
/// <c>MyValidator.Validate(value)</c> — no Activator.CreateInstance, no virtual dispatch, AOT-safe.
/// </summary>
public interface IValidator<T>
{
    static abstract ValidationResult Validate(T value);
}

/// <summary>
/// Asynchronous validator. ValueTask return type lets sync-completing validators avoid the state-machine
/// allocation entirely when awaited with <c>ConfigureAwait(false)</c>.
/// </summary>
public interface IAsyncValidator<T>
{
    static abstract System.Threading.Tasks.ValueTask<ValidationResult> ValidateAsync(
        T value,
        System.Threading.CancellationToken ct);
}
