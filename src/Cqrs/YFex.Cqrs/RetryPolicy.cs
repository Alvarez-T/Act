namespace YFex.Cqrs;

public enum BackoffStrategy
{
    Linear,
    Exponential
}

public sealed record RetryPolicy(
    int MaxAttempts,
    BackoffStrategy Backoff = BackoffStrategy.Exponential,
    TimeSpan? InitialDelay = null,
    TimeSpan? MaxDelay = null)
{
    public static RetryPolicy Default => new(3);
}

public sealed class RetryConfigurationBuilder
{
    private int _maxAttempts = 3;
    private BackoffStrategy _backoff = BackoffStrategy.Exponential;
    private TimeSpan? _initialDelay;
    private TimeSpan? _maxDelay;

    public RetryConfigurationBuilder MaxAttempts(int count) { _maxAttempts = count; return this; }
    public RetryConfigurationBuilder Backoff(BackoffStrategy strategy) { _backoff = strategy; return this; }
    public RetryConfigurationBuilder InitialDelay(TimeSpan delay) { _initialDelay = delay; return this; }
    public RetryConfigurationBuilder MaxDelay(TimeSpan delay) { _maxDelay = delay; return this; }

    internal RetryPolicy Build() => new(_maxAttempts, _backoff, _initialDelay, _maxDelay);
}
