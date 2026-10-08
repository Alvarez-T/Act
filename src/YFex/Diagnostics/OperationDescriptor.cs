using YFex.Helpers;

namespace YFex.Diagnostics;

/// <summary>How a captured field is written. Shown in the reviewed <c>LogSchema.verified.txt</c> snapshot (logging 04 §3).</summary>
public enum FieldCapture : byte
{
    /// <summary>The typed value.</summary>
    Value = 0,

    /// <summary>Free text, truncated and scrubbed on the consumer.</summary>
    Scrubbed = 1,

    /// <summary>Collections: the item count only.</summary>
    Count = 2,

    /// <summary>Blobs and streams: the length only.</summary>
    Length = 3,

    /// <summary>A nested record/class through its log shape.</summary>
    Shape = 4,

    /// <summary>Redacted per the data's sensitivity (pseudonym, mask) on the consumer.</summary>
    Redacted = 5,

    /// <summary>Never recorded (secret-looking member, financial or secret data).</summary>
    Erased = 6,
}

/// <summary>
/// Metadata of one captured field: its name (UTF-16 key used by sinks, <c>total</c>, <c>itens.count</c>) and how it is
/// written. Data sensitivity will be added here once the YFex.Privacy core contract exists (logging L14).
/// </summary>
public readonly record struct FieldSpec(string Name, FieldCapture Capture = FieldCapture.Value)
{
    /// <summary>The member's type as shown in the schema snapshot (<c>Money</c>, <c>List&lt;Item&gt;</c>); optional.</summary>
    public string? TypeName { get; init; }

    /// <summary>True when the server indexes this field for search (<c>[LogValue(SearchKey = true)]</c> types).</summary>
    public bool IsSearchKey { get; init; }
}

/// <summary>
/// Compile-time metadata of an operation type (message, route, rule set, saga, job, <c>[Log]</c> method), generated once and
/// registered through <see cref="OperationRegistry"/>. At run time a record holds only <see cref="Index"/> and raw values;
/// strings are resolved on the consumer thread.
/// </summary>
public class OperationDescriptor
{
    private readonly FieldSpec[] _fields;

    public OperationDescriptor(
        OperationKind kind,
        string name,
        string module,
        LogLevel level = LogLevel.Information,
        OperationDescriptorFlags flags = OperationDescriptorFlags.None,
        FieldSpec[]? fields = null,
        string? template = null,
        uint eventId = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Kind = kind.IsEmpty ? OperationKind.Custom : kind;
        Name = name;
        Module = module ?? string.Empty;
        Level = level;
        Flags = flags;
        _fields = fields ?? [];
        Template = template;
        EventId = eventId != 0 ? eventId : ComputeEventId(name);
        Index = -1;
    }

    /// <summary>
    /// Dense process-local index assigned by <see cref="OperationRegistry.Register{TDescriptor}"/>: engines keep per-operation
    /// thresholds, counters and histograms in arrays indexed by it. -1 until registered.
    /// </summary>
    public int Index { get; private set; }

    /// <summary>Stable across releases: the low 32 bits of <see cref="StableHash64"/> of <see cref="Name"/>, unless set by <c>[LogId]</c>.</summary>
    public uint EventId { get; }

    public OperationKind Kind { get; }

    /// <summary>Stable name (<c>Venda.Finalizar</c>, <c>Route:Caixa</c>, <c>SefazLog.Rejeitada</c>).</summary>
    public string Name { get; }

    /// <summary>Assembly or module name.</summary>
    public string Module { get; }

    /// <summary>Message template of <c>[Log]</c> methods; null for operations.</summary>
    public string? Template { get; }

    /// <summary>Default level. Configuration can override it at run time through the engine's thresholds.</summary>
    public LogLevel Level { get; }

    public OperationDescriptorFlags Flags { get; }

    /// <summary>Captured fields, indexed by the field ids the capture code writes.</summary>
    public ReadOnlySpan<FieldSpec> Fields => _fields;

    /// <summary>
    /// True for descriptors created at run time instead of generated: fallbacks for types without a generated descriptor and
    /// ad-hoc operations (<c>log.Operation("name")</c>). Reviewed schemas (<c>LogSchema.verified.txt</c>) leave them out because
    /// they depend on what ran.
    /// </summary>
    public bool IsCreatedAtRunTime { get; init; }

    /// <summary>True when this descriptor has the flag.</summary>
    public bool Has(OperationDescriptorFlags flag) => (Flags & flag) != 0;

    public override string ToString() => Name;

    internal bool TryAssignIndex(int index)
    {
        if (Index >= 0) return false;
        Index = index;
        return true;
    }

    /// <summary>The default event id of a name: low 32 bits of its <see cref="StableHash64"/> (never 0).</summary>
    public static uint ComputeEventId(string name)
    {
        uint id = unchecked((uint)StableHash64.Hash(name.AsSpan()));
        return id == 0 ? 1u : id;
    }
}

/// <summary>Captures the fields of <typeparamref name="T"/> without boxing (generated).</summary>
public delegate void OperationCapture<T>(in T value, ref LogWriter writer);

/// <summary>Typed descriptor: knows how to capture the fields of a <typeparamref name="T"/> message without boxing.</summary>
public sealed class OperationDescriptor<T> : OperationDescriptor
{
    public OperationDescriptor(
        OperationKind kind,
        string name,
        string module,
        LogLevel level = LogLevel.Information,
        OperationDescriptorFlags flags = OperationDescriptorFlags.None,
        FieldSpec[]? fields = null,
        OperationCapture<T>? capture = null,
        string? template = null,
        uint eventId = 0)
        : base(kind, name, module, level, flags, fields, template, eventId)
        => Capture = capture;

    /// <summary>Capture code; null means "shape only" (name, duration, outcome, context; no fields).</summary>
    public OperationCapture<T>? Capture { get; }
}

/// <summary>
/// Static generic slot: a dispatcher finds the descriptor of <c>TCommand</c> with one static load. Set by generated module
/// initializers; types without a generated descriptor get one lazily from their type name (<see cref="GetOrCreate"/>).
/// </summary>
public static class OperationDescriptorFor<T>
{
    private static OperationDescriptor<T>? s_descriptor;

    /// <summary>The registered descriptor, or null.</summary>
    public static OperationDescriptor<T>? Descriptor
    {
        get => Volatile.Read(ref s_descriptor);
        set => Volatile.Write(ref s_descriptor, value);
    }

    /// <summary>
    /// The registered descriptor, or a fallback created once from the type name with kind <paramref name="kind"/>
    /// (<see cref="OperationKind.Custom"/> when empty). The fallback has no capture.
    /// </summary>
    public static OperationDescriptor<T> GetOrCreate(OperationKind kind)
    {
        OperationDescriptor<T>? existing = Volatile.Read(ref s_descriptor);
        return existing ?? CreateFallback(kind);
    }

    private static OperationDescriptor<T> CreateFallback(OperationKind kind)
    {
        Type type = typeof(T);
        string module = type.Assembly.GetName().Name ?? string.Empty;
        var created = new OperationDescriptor<T>(kind, FallbackName(type), module) { IsCreatedAtRunTime = true };
        OperationDescriptor<T>? raced = Interlocked.CompareExchange(ref s_descriptor, created, null);
        return raced ?? OperationRegistry.Register(created);
    }

    // "Outer.Inner" for nested types, the generic definition name without arity otherwise.
    private static string FallbackName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`');
        if (tick > 0) name = name[..tick];
        return type.DeclaringType is { } outer ? FallbackName(outer) + "." + name : name;
    }
}
