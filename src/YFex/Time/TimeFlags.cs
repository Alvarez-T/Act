namespace YFex.Time;

/// <summary>
/// How much a <see cref="TrustedInstant"/> can be trusted (time README §2.1). Stored in envelopes and audit records, so values are
/// fixed forever.
/// </summary>
[Flags]
public enum TimeFlags : byte
{
    /// <summary>Anchored to a source, nothing unusual.</summary>
    None = 0,

    /// <summary>No source has answered since this process started (the value comes from the device clock or the high-water mark).</summary>
    Unanchored = 1,

    /// <summary>The device clock went back behind trusted time, or was behind the stored high-water mark at start-up.</summary>
    RollbackDetected = 2,

    /// <summary>A source was dropped by the vote, or refused (e.g. an unauthenticated source trying to pull time back).</summary>
    SourcesDisagree = 4,

    /// <summary>No authenticated source backs the value (NTP, HTTPS Date, GNSS, peers, or no anchor at all).</summary>
    Unauthenticated = 8,

    /// <summary>The device clock jumped forward, or the monotonic clock paused (sleep on Linux/macOS).</summary>
    ClockAdjusted = 16,

    /// <summary>
    /// The value is bounded below by a high-water mark that was written while an authenticated anchor was active (time ⚑T11): after
    /// an offline start, real time is known to be at least that.
    /// </summary>
    AuthenticatedFloor = 32,
}
