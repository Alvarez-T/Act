using System.Runtime.CompilerServices;

namespace YFex.Diagnostics;

/// <summary>
/// The engine behind <see cref="Operations"/>, installed once per process by the composition root (YFex.Logging's
/// <c>AddYFexLogging</c>). Libraries never see it. Implementations must not throw.
/// </summary>
public interface IOperationEngine
{
    /// <summary>The ambient position (logging 05 §1); <c>default</c> outside any operation.</summary>
    TraceContext Current { get; }

    /// <summary>Whether a record of <paramref name="descriptor"/> at <paramref name="level"/> would be kept.</summary>
    bool IsEnabled(OperationDescriptor descriptor, LogLevel level);

    /// <summary>Opens an operation without captured fields.</summary>
    ActiveOperation Start(OperationDescriptor descriptor);

    /// <summary>Opens an operation and captures <paramref name="value"/>'s fields through <see cref="OperationDescriptor{T}.Capture"/>.</summary>
    ActiveOperation Start<T>(OperationDescriptor<T> descriptor, in T value);

    /// <summary>Opens a new root operation linked (not parented) to another one: outbox replay, catch-up, background work.</summary>
    ActiveOperation StartLinked(OperationDescriptor descriptor, in OperationLink link);

    /// <summary>Writes an instant fact (breadcrumb, cache miss, circuit opened).</summary>
    void Mark(OperationDescriptor descriptor, LogLevel level, OperationOutcome outcome, int code);

    /// <summary>Writes an instant fact with captured fields.</summary>
    void Mark<T>(OperationDescriptor<T> descriptor, in T value, LogLevel level, OperationOutcome outcome, int code);
}

/// <summary>
/// The probe hub used by generated code and library choke points. With no engine installed every member is a no-operation
/// after one static load: 0 bytes, about a nanosecond. This is how libraries are instrumented against YFex core only and
/// never reference YFex.Logging (logging principle C).
/// </summary>
/// <example>
/// <code>
/// var operation = Operations.Start(in command);
/// try { var r = await next(command, ct); operation.Complete(r.ToOutcome()); return r; }
/// catch (OperationCanceledException) when (ct.IsCancellationRequested) { operation.Complete(OperationOutcome.Cancelled); throw; }
/// catch (Exception ex) { operation.Fail(ex); throw; }
/// </code>
/// </example>
public static class Operations
{
    private static IOperationEngine? s_engine;
    private static long s_internalErrors;

    /// <summary>True when an engine is installed.</summary>
    public static bool IsInstalled => Volatile.Read(ref s_engine) is not null;

    /// <summary>The installed engine, or null.</summary>
    public static IOperationEngine? Engine => Volatile.Read(ref s_engine);

    /// <summary>Exceptions thrown by the engine and swallowed by the probes (the engine reports details to its self-log).</summary>
    public static long InternalErrorCount => Interlocked.Read(ref s_internalErrors);

    /// <summary>
    /// Installs the process engine. Composition roots only (YFex.Logging calls it). A second install throws unless
    /// <paramref name="allowReinstall"/> is set (isolated test collections).
    /// </summary>
    public static void Install(IOperationEngine engine, bool allowReinstall = false)
    {
        ArgumentNullException.ThrowIfNull(engine);
        IOperationEngine? previous = Interlocked.CompareExchange(ref s_engine, engine, null);
        if (previous is null || ReferenceEquals(previous, engine)) return;
        if (!allowReinstall)
            throw new InvalidOperationException("An operation engine is already installed in this process. Use allowReinstall only in isolated tests.");
        Volatile.Write(ref s_engine, engine);
    }

    /// <summary>Removes <paramref name="engine"/> if it is the installed one (engine shutdown, tests).</summary>
    public static bool Uninstall(IOperationEngine engine)
        => ReferenceEquals(Interlocked.CompareExchange(ref s_engine, null, engine), engine);

    /// <summary>
    /// Raised when engine code threw inside a probe (the probe swallowed it). Engines forward it to their self-log; handlers
    /// must not throw and must not log through the pipeline.
    /// </summary>
    public static event Action<Exception>? InternalError;

    /// <summary>Called by <see cref="OperationHandle"/> and engines when engine code threw inside a probe.</summary>
    public static void ReportInternalError(Exception exception)
    {
        Interlocked.Increment(ref s_internalErrors);
        try
        {
            InternalError?.Invoke(exception);
        }
        catch
        {
            // Never propagate out of a probe.
        }
    }

    // ── Checks ───────────────────────────────────────────────────────────────

    /// <summary>The ambient position in the current trace; <c>default</c> without an engine or outside any operation.</summary>
    public static TraceContext Current
    {
        get
        {
            IOperationEngine? engine = s_engine;
            return engine is null ? default : engine.Current;
        }
    }

    /// <summary>One static load and a compare when no engine is installed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(OperationDescriptor descriptor, LogLevel level)
    {
        IOperationEngine? engine = s_engine;
        return engine is not null && engine.IsEnabled(descriptor, level);
    }

    /// <summary>Whether operations of <typeparamref name="T"/> at <paramref name="level"/> would be kept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled<T>(LogLevel level)
    {
        IOperationEngine? engine = s_engine;
        return engine is not null && OperationDescriptorFor<T>.Descriptor is { } descriptor && engine.IsEnabled(descriptor, level);
    }

    // ── Operations ───────────────────────────────────────────────────────────

    /// <summary>
    /// Opens an operation for <paramref name="message"/> using <see cref="OperationDescriptorFor{T}"/>. Unregistered types get
    /// a fallback descriptor named after the type, of kind <paramref name="kindIfUnregistered"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ActiveOperation Start<T>(in T message, OperationKind kindIfUnregistered = default)
    {
        IOperationEngine? engine = s_engine;
        return engine is null ? default : StartCore(engine, OperationDescriptorFor<T>.GetOrCreate(kindIfUnregistered), in message);
    }

    /// <summary>Opens an operation with an explicit typed descriptor and captures <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ActiveOperation Start<T>(OperationDescriptor<T> descriptor, in T value)
    {
        IOperationEngine? engine = s_engine;
        return engine is null ? default : StartCore(engine, descriptor, in value);
    }

    /// <summary>Opens an operation without captured fields (routes, state commands, jobs).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ActiveOperation Start(OperationDescriptor descriptor)
    {
        IOperationEngine? engine = s_engine;
        return engine is null ? default : StartCore(engine, descriptor);
    }

    /// <summary>Opens a new root linked to another operation (offline replay, catch-up, background work; logging 05 §4).</summary>
    public static ActiveOperation StartLinked(OperationDescriptor descriptor, in OperationLink link)
    {
        IOperationEngine? engine = s_engine;
        if (engine is null) return default;
        try
        {
            return engine.StartLinked(descriptor, in link);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
            return default;
        }
    }

    // ── Marks ────────────────────────────────────────────────────────────────

    /// <summary>Writes an instant fact. <paramref name="code"/> is an <see cref="OutcomeCodes"/> id.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Mark(OperationDescriptor descriptor, LogLevel level, OperationOutcome outcome = OperationOutcome.Ok, int code = 0)
    {
        IOperationEngine? engine = s_engine;
        if (engine is not null) MarkCore(engine, descriptor, level, outcome, code);
    }

    /// <summary>Writes an instant fact with the captured fields of <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void MarkWith<T>(OperationDescriptor<T> descriptor, in T value, LogLevel level,
        OperationOutcome outcome = OperationOutcome.Ok, int code = 0)
    {
        IOperationEngine? engine = s_engine;
        if (engine is not null) MarkWithCore(engine, descriptor, in value, level, outcome, code);
    }

    // ── Slow paths (engine installed): kept out of line so the disabled path stays a load and a branch ──

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ActiveOperation StartCore<T>(IOperationEngine engine, OperationDescriptor<T> descriptor, in T value)
    {
        try
        {
            return engine.Start(descriptor, in value);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
            return default;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ActiveOperation StartCore(IOperationEngine engine, OperationDescriptor descriptor)
    {
        try
        {
            return engine.Start(descriptor);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
            return default;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MarkCore(IOperationEngine engine, OperationDescriptor descriptor, LogLevel level, OperationOutcome outcome, int code)
    {
        try
        {
            engine.Mark(descriptor, level, outcome, code);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MarkWithCore<T>(IOperationEngine engine, OperationDescriptor<T> descriptor, in T value, LogLevel level,
        OperationOutcome outcome, int code)
    {
        try
        {
            engine.Mark(descriptor, in value, level, outcome, code);
        }
        catch (Exception exception)
        {
            ReportInternalError(exception);
        }
    }
}
