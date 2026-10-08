namespace YFex.Time;

/// <summary>
/// A clock that knows how far it can be trusted (time ⚑T8). Implemented by the YFex.Time library's <c>TrustedTimeProvider</c>, which
/// is also the registered <see cref="TimeProvider"/>. Consumers keep injecting <see cref="TimeProvider"/> and call
/// <see cref="TrustedClockExtensions.GetTrustedNow"/> / <see cref="TrustedClockExtensions.WaitForAnchorAsync"/>.
/// </summary>
public interface ITrustedClock
{
    /// <summary>Best estimate of UTC now with its uncertainty, source and flags. Synchronous, no I/O.</summary>
    TrustedInstant GetTrustedNow();

    /// <summary>
    /// Returns as soon as a source anchored the clock (asking for a synchronization when there is none yet), or when
    /// <paramref name="timeout"/> elapses — then with the current, still unanchored value. Never throws for a timeout.
    /// </summary>
    ValueTask<TrustedInstant> WaitForAnchorAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

/// <summary>Trusted time from any <see cref="TimeProvider"/> (time ⚑T8).</summary>
public static class TrustedClockExtensions
{
    /// <summary>
    /// The provider's trusted time when it is an <see cref="ITrustedClock"/> (the YFex.Time library); otherwise its plain time flagged
    /// <see cref="TimeFlags.Unanchored"/> | <see cref="TimeFlags.Unauthenticated"/> — so code written against <see cref="TimeProvider"/>
    /// works with <see cref="TimeProvider.System"/> and test fakes, and security checks refuse it.
    /// <code>
    /// TrustedInstant now = time.GetTrustedNow();
    /// if (!now.IsUsableForSecurity(TimeSpan.FromMinutes(10))) return SnapshotUse.Refused;
    /// </code>
    /// </summary>
    public static TrustedInstant GetTrustedNow(this TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return time is ITrustedClock trusted ? trusted.GetTrustedNow() : TrustedInstant.FromUntrustedClock(time.GetUtcNow());
    }

    /// <summary>
    /// <see cref="ITrustedClock.WaitForAnchorAsync"/> when the provider is a trusted clock; otherwise its plain time immediately (there
    /// is nothing to wait for).
    /// <code>
    /// TrustedInstant now = await time.WaitForAnchorAsync(TimeSpan.FromSeconds(3), ct);   // first NFC-e of the shift
    /// if (!now.IsAnchored) warnings.Add("Horário do PDV não confirmado");
    /// </code>
    /// </summary>
    public static ValueTask<TrustedInstant> WaitForAnchorAsync(this TimeProvider time, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(time);
        return time is ITrustedClock trusted
            ? trusted.WaitForAnchorAsync(timeout, cancellationToken)
            : ValueTask.FromResult(TrustedInstant.FromUntrustedClock(time.GetUtcNow()));
    }
}
