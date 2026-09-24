namespace YFex.Cqrs.Configuration;

/// <summary>
/// Folds the command builder's policy verbs into a single <c>ResilienceSpec</c> at BuildMetadata time
/// (design §1 L2, §14.6): the legacy <c>.Retry</c>/<c>.Timeout</c> knobs are mapped into the unified vocabulary
/// alongside <c>.UseResource</c>/<c>.Fallback</c>/<c>.Hedging</c>, so a single spec drives the resilience
/// pipeline. Returns null when no verb was used (the common case → zero behavior change).
///
/// Types are fully qualified: this file sits in <c>YFex.Cqrs.Configuration</c>, where the enclosing
/// <c>YFex.Cqrs</c> namespace's own <c>RetryPolicy</c>/<c>BackoffStrategy</c> would otherwise shadow the
/// resilience ones.
/// </summary>
internal static class ResilienceMapping
{
    public static YFex.Resilience.ResilienceSpec? Compose(
        YFex.Cqrs.RetryPolicy? retry,
        System.TimeSpan? timeout,
        string? resourceName,
        YFex.Resilience.FallbackPolicy? fallback,
        YFex.Resilience.HedgingPolicy? hedging)
    {
        if (retry is null && timeout is null && resourceName is null && fallback is null && hedging is null)
            return null;

        YFex.Resilience.RetryPolicy? mappedRetry = retry is null
            ? null
            : new YFex.Resilience.RetryPolicy(
                MaxAttempts: retry.MaxAttempts,
                Backoff: retry.Backoff == YFex.Cqrs.BackoffStrategy.Linear
                    ? YFex.Resilience.BackoffStrategy.Linear
                    : YFex.Resilience.BackoffStrategy.Exponential,
                InitialDelay: retry.InitialDelay ?? System.TimeSpan.FromMilliseconds(100),
                MaxDelay: retry.MaxDelay ?? System.TimeSpan.FromSeconds(5),
                Jitter: true);

        YFex.Resilience.TimeoutPolicy? mappedTimeout =
            timeout is null ? null : new YFex.Resilience.TimeoutPolicy(timeout.Value);

        return new YFex.Resilience.ResilienceSpec(mappedRetry, mappedTimeout, fallback, hedging, resourceName);
    }
}
