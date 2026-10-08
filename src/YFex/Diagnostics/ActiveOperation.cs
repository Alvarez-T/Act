using System.Runtime.CompilerServices;

namespace YFex.Diagnostics;

/// <summary>
/// Token for an open operation, returned by <see cref="Operations.Start{T}(in T, OperationKind)"/>. A plain struct (not a
/// <c>ref struct</c>) because generated async code holds it across <c>await</c>.
/// <para>
/// Safe to copy: <see cref="Complete(OperationOutcome, int, OperationFlags)"/> and <see cref="Fail(Exception)"/> are idempotent
/// through a generation compare-and-swap on the engine's pooled <see cref="OperationHandle"/>, so a double end writes one
/// record and a stale copy can never end a recycled operation. <c>default</c> is inactive and every member is a no-op.
/// </para>
/// </summary>
public readonly struct ActiveOperation
{
    private readonly OperationHandle? _handle;
    private readonly int _generation;

    internal ActiveOperation(OperationHandle handle, int generation)
    {
        _handle = handle;
        _generation = generation;
    }

    /// <summary>True while the operation is open (an engine is installed, the operation is recorded, and it has not ended).</summary>
    public bool IsActive => _handle is not null && _handle.IsOpen(_generation);

    /// <summary>The operation's position in its trace (store it with outbox entries); empty when inactive.</summary>
    public TraceContext Context => _handle is not null && _handle.IsOpen(_generation) ? _handle.Context : default;

    /// <summary>Ends the operation. <paramref name="code"/> is an id from <see cref="OutcomeCodes.Intern"/> (0 = none).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Complete(OperationOutcome outcome = OperationOutcome.Ok, int code = 0, OperationFlags flags = OperationFlags.None)
        => _handle?.End(_generation, outcome, code, flags, null);

    /// <summary>Ends the operation with a text outcome code (interned; generated code caches the id per call site instead).</summary>
    public void Complete(OperationOutcome outcome, string? code, OperationFlags flags = OperationFlags.None)
    {
        if (_handle is { } handle) handle.End(_generation, outcome, OutcomeCodes.Intern(code), flags, null);
    }

    /// <summary>Ends the operation as <see cref="OperationOutcome.Faulted"/>, keeping the exception by reference.</summary>
    public void Fail(Exception error) => _handle?.End(_generation, OperationOutcome.Faulted, 0, OperationFlags.None, error);

    /// <summary>Ends the operation with an exception and an explicit outcome (for example <see cref="OperationOutcome.TimedOut"/>).</summary>
    public void Fail(Exception error, OperationOutcome outcome, OperationFlags flags = OperationFlags.None)
        => _handle?.End(_generation, outcome, 0, flags, error);

    /// <summary>Adds a late fact to the open operation (rows affected, item count). Field id as in the descriptor's fields.</summary>
    public void Annotate(ushort field, long value) => _handle?.Annotate(_generation, field, null, value);

    /// <summary>Adds a late named fact (pipeline stages: <c>violations=2</c>). Pass a literal: the reference is stored.</summary>
    public void Annotate(string name, long value) => _handle?.Annotate(_generation, LogWriter.NoField, name, value);
}

/// <summary>
/// Engine-side state of one open operation (pooled and reused). This base class owns the generation counter that makes
/// <see cref="ActiveOperation"/> tokens idempotent; engines implement what happens on end and annotation.
/// </summary>
public abstract class OperationHandle
{
    private int _generation;

    /// <summary>The operation's position in its trace while it is open.</summary>
    public abstract TraceContext Context { get; }

    /// <summary>Creates the token for the operation the engine just prepared in this handle.</summary>
    protected ActiveOperation Open() => new(this, Volatile.Read(ref _generation));

    /// <summary>True while a token of <paramref name="generation"/> may still end this handle.</summary>
    internal bool IsOpen(int generation) => Volatile.Read(ref _generation) == generation;

    /// <summary>
    /// Ends the operation. Runs <see cref="OnEnded"/> at most once per generation (compare-and-swap); later or stale calls do
    /// nothing. Never throws: engine failures are counted in <see cref="Operations.InternalErrorCount"/>.
    /// </summary>
    internal void End(int generation, OperationOutcome outcome, int code, OperationFlags flags, Exception? error)
    {
        if (Interlocked.CompareExchange(ref _generation, unchecked(generation + 1), generation) != generation) return;
        try
        {
            OnEnded(outcome, code, flags, error);
        }
        catch (Exception exception)
        {
            Operations.ReportInternalError(exception);
        }
    }

    internal void Annotate(int generation, ushort field, string? name, long value)
    {
        if (Volatile.Read(ref _generation) != generation) return;
        try
        {
            OnAnnotated(field, name, value);
        }
        catch (Exception exception)
        {
            Operations.ReportInternalError(exception);
        }
    }

    /// <summary>
    /// Writes the end record and returns the handle to its pool. Called exactly once per opened generation. The generation has
    /// already advanced, so the handle may be reused as soon as this method releases it.
    /// </summary>
    protected abstract void OnEnded(OperationOutcome outcome, int code, OperationFlags flags, Exception? error);

    /// <summary>Records a late fact. <paramref name="name"/> is set when <paramref name="field"/> is <see cref="LogWriter.NoField"/>.</summary>
    protected abstract void OnAnnotated(ushort field, string? name, long value);
}

/// <summary>
/// Process-wide table of outcome codes (<c>VAL.REQUIRED</c>, <c>SEFAZ.539</c>): records carry the int id, the consumer resolves
/// the text. Id 0 means "no code". Generated code interns once per call site.
/// </summary>
public static class OutcomeCodes
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, int> s_ids = new(StringComparer.Ordinal);
    private static string[] s_codes = new string[64];
    private static int s_count = 1;

    /// <summary>The id of <paramref name="code"/> (registered on first use). Null or empty gives 0.</summary>
    public static int Intern(string? code)
    {
        if (string.IsNullOrEmpty(code)) return 0;
        lock (s_lock)
        {
            if (s_ids.TryGetValue(code, out int id)) return id;
            id = s_count;
            string[] codes = s_codes;
            if (id == codes.Length)
            {
                var grown = new string[codes.Length * 2];
                codes.CopyTo(grown, 0);
                codes = grown;
            }
            codes[id] = code;
            s_ids[code] = id;
            Volatile.Write(ref s_codes, codes);
            Volatile.Write(ref s_count, id + 1);
            return id;
        }
    }

    /// <summary>The text of an id; null for 0 or unknown ids.</summary>
    public static string? NameOf(int id)
    {
        string[] codes = Volatile.Read(ref s_codes);
        return id > 0 && id < Volatile.Read(ref s_count) && id < codes.Length ? codes[id] : null;
    }
}
