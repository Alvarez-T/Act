namespace YFex.Diagnostics;

/// <summary>
/// Severity of a record. Same numeric values as <c>Microsoft.Extensions.Logging.LogLevel</c>, so the inbound bridge is a
/// cast (logging ⚑L7). Audit is not a level: it is a <see cref="LogChannel"/> (a denied login is <see cref="Warning"/> on the
/// audit channel).
/// </summary>
public enum LogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,

    /// <summary>Disables a descriptor, kind or module in thresholds. Never the level of a record.</summary>
    None = 6,
}

/// <summary>Where a record goes. Each channel has its own loss and retention guarantees (logging README).</summary>
public enum LogChannel : byte
{
    /// <summary>What the system did and why it failed. May drop under pressure (counted).</summary>
    Diagnostic = 0,

    /// <summary>
    /// Correlation marker only. Durable audit belongs to the sibling library YFex.Audit (logging L20); the logging engine
    /// never acts as an audit store.
    /// </summary>
    Audit = 1,

    /// <summary>Aggregated metric snapshots.</summary>
    Metric = 2,

    /// <summary>Pipeline control records (heartbeats, drop summaries, configuration changes).</summary>
    Control = 3,
}
