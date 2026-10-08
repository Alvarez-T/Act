namespace YFex.Diagnostics;

/// <summary>How an operation ended. Detail goes into an interned outcome code (e.g. <c>VAL.REQUIRED</c>, <c>SEFAZ.539</c>).</summary>
public enum OperationOutcome : byte
{
    /// <summary>Still open, orphaned, or disposed without an explicit end.</summary>
    Unknown = 0,

    /// <summary>Success. Cache provenance goes into <see cref="OperationFlags"/>.</summary>
    Ok = 1,

    /// <summary>Accepted offline (queued in the outbox).</summary>
    Queued = 2,

    /// <summary>Expected failure: validation, rule or business error result.</summary>
    Rejected = 3,

    /// <summary>Authorization or tenancy gate.</summary>
    Denied = 4,

    /// <summary>Concurrency conflict (event-sourcing version, sync conflict).</summary>
    Conflict = 5,

    /// <summary>Cancelled through a <see cref="CancellationToken"/>.</summary>
    Cancelled = 6,

    /// <summary>Timeout policy.</summary>
    TimedOut = 7,

    /// <summary>Unexpected exception.</summary>
    Faulted = 8,
}

/// <summary>Facts about how an operation ran, recorded with its end.</summary>
[Flags]
public enum OperationFlags : ushort
{
    None = 0,
    CacheHit = 1 << 0,
    Stale = 1 << 1,
    Offline = 1 << 2,
    Retried = 1 << 3,

    /// <summary>Outbox or event-sourcing replay.</summary>
    Replayed = 1 << 4,
    Compensated = 1 << 5,
    Sampled = 1 << 6,

    /// <summary>Emitted later by the flight recorder (tail-based retroactive logging).</summary>
    Retro = 1 << 7,

    /// <summary>Some fields did not fit the record and were dropped.</summary>
    Truncated = 1 << 8,
}

/// <summary>Static behaviour of an operation type, set by the generator or the composition root.</summary>
[Flags]
public enum OperationDescriptorFlags : ushort
{
    None = 0,

    /// <summary>Every end record is also marked for the audit channel (correlation; the durable trail is YFex.Audit).</summary>
    Audit = 1 << 0,

    /// <summary>Emit a start record too (jobs, sagas): in-progress operations are visible and orphans detectable.</summary>
    LongRunning = 1 << 1,

    /// <summary>Starts a span even at <c>Boundaries</c> granularity (RPC, HTTP, command, job).</summary>
    Boundary = 1 << 2,

    /// <summary>Record only, never a span parent (validation per field, property change).</summary>
    Leaf = 1 << 3,

    /// <summary>Kept in the per-session breadcrumb ring (navigation, UI actions).</summary>
    Breadcrumb = 1 << 4,

    /// <summary>Instrumentation switched off for this type (<c>[NoLog]</c>): only counted in metrics.</summary>
    Disabled = 1 << 5,
}
