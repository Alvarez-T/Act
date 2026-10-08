namespace YFex.Diagnostics;

// Exceptions only (logging 04 §3): capture is automatic and decided by member types. These live in YFex core so contract
// assemblies (*.Public) can use them without referencing the logging engine.

/// <summary>
/// Never captured. On a member: the member is left out of the captured fields. On a type: no descriptor is generated
/// (the operation is still counted in metrics).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Property
              | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
public sealed class NoLogAttribute : Attribute;

/// <summary>Stable operation name, kept across type renames (dashboards and alert rules use names).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class LogNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Explicit event id, kept across renames (instead of the hash of the name).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class LogIdAttribute(uint id) : Attribute
{
    public uint Id { get; } = id;
}

/// <summary>
/// Marks an operation as audit-relevant (<see cref="OperationDescriptorFlags.Audit"/>). Logging uses it only to correlate;
/// the durable audit trail is written by YFex.Audit, which reads the same marker.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class AuditedAttribute : Attribute;

/// <summary>Long-running operation (job, saga): a start record is written too, so in-progress and orphaned runs are visible.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class LongRunningAttribute : Attribute;

/// <summary>Default level of an operation type (configuration can still override it at run time).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Method, Inherited = false)]
public sealed class LogLevelAttribute(LogLevel level) : Attribute
{
    public LogLevel Level { get; } = level;
}

/// <summary>Captures the first <see cref="Count"/> items of a collection member (default: the count only).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
public sealed class LogItemsAttribute(int count) : Attribute
{
    public int Count { get; } = count;
}

/// <summary>How much of a message the generators capture by default.</summary>
public enum LogCaptureMode : byte
{
    /// <summary>Every member, by type (logging 04 §3).</summary>
    Auto = 0,

    /// <summary>Search keys and numbers only.</summary>
    KeysOnly = 1,

    /// <summary>Shape only: name, duration, outcome and context, no fields.</summary>
    None = 2,
}

/// <summary>Narrows automatic capture for an assembly or a type.</summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class LogCaptureAttribute(LogCaptureMode mode) : Attribute
{
    public LogCaptureMode Mode { get; } = mode;
}
