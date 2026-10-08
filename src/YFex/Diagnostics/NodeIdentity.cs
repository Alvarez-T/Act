namespace YFex.Diagnostics;

/// <summary>What kind of machine or process a node is.</summary>
public enum NodeKind : byte
{
    Server = 0,
    Desktop = 1,
    Mobile = 2,
    Browser = 3,
    Worker = 4,

    /// <summary>Weak hardware (POS terminal, tablet, kiosk).</summary>
    Device = 5,
}

/// <summary>
/// Who produced a stream of records. Sent once per file, connection or batch, never per record (logging 02 §6).
/// </summary>
/// <param name="App">Application name (<c>farma-pdv</c>).</param>
/// <param name="Version">Release (<c>1.4.2+sha</c>).</param>
/// <param name="Environment">Deployment environment (<c>prod</c>, <c>Development</c>).</param>
/// <param name="InstanceId">Stable per installation (persisted), survives restarts.</param>
/// <param name="Host">Machine name.</param>
/// <param name="ProcessId">Operating-system process id.</param>
/// <param name="StartedAt">Process start.</param>
/// <param name="Kind">Machine kind.</param>
/// <param name="Region">Optional region or site.</param>
/// <param name="DeviceId">Optional device id (joins YFex.Profiling's hardware fingerprint).</param>
public sealed record NodeIdentity(
    string App,
    string Version,
    string Environment,
    Guid InstanceId,
    string Host,
    int ProcessId,
    DateTimeOffset StartedAt,
    NodeKind Kind,
    string? Region = null,
    string? DeviceId = null);

/// <summary>
/// Static generic slot for value objects that write themselves (<see cref="ILogValue{TSelf}"/>), so code that only knows an
/// unconstrained <typeparamref name="T"/> (interpolated-string holes, log shapes) can reach the implementation without boxing.
/// Filled by generated module initializers (<c>[LogValue]</c>) and by <see cref="Register{TValue}"/>.
/// </summary>
public static class LogValueWriterFor<T>
{
    /// <summary>The writer, or null when <typeparamref name="T"/> does not implement <see cref="ILogValue{TSelf}"/> (or was not registered).</summary>
    public static LogValueWriter<T>? Write { get; private set; }

    /// <summary>True when the type was registered as a search key.</summary>
    public static bool IsSearchKey { get; private set; }

    internal static void Set(LogValueWriter<T> write, bool isSearchKey)
    {
        Write = write;
        IsSearchKey = isSearchKey;
    }
}

/// <summary>Writes a value as one field.</summary>
public delegate void LogValueWriter<T>(in T value, ref LogWriter writer, ushort field);

/// <summary>Registration helpers for <see cref="LogValueWriterFor{T}"/>.</summary>
public static class LogValueWriters
{
    /// <summary>Registers <typeparamref name="TValue"/>'s <see cref="ILogValue{TSelf}"/> implementation (idempotent).</summary>
    public static void Register<TValue>() where TValue : ILogValue<TValue>
        => LogValueWriterFor<TValue>.Set(static (in TValue value, ref LogWriter writer, ushort field) => TValue.Write(in value, ref writer, field),
            TValue.IsSearchKey);

#pragma warning disable CA2255 // Library module initializer on purpose: core value types must be registered before any log call.
    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void RegisterCoreTypes()
    {
        // Core value types: the raw-value ones are also reachable through RawValueConvert; registering them here makes the
        // lookup a single static load for every one of them.
        Register<Money>();
        Register<Percentual>();
        Register<Quantity>();
        Register<Mass>();
        Register<Length>();
        Register<FieldPath>();
        Register<ValidityPeriod>();
    }
#pragma warning restore CA2255
}
