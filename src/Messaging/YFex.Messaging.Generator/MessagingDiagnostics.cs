using Microsoft.CodeAnalysis;

namespace YFex.Messaging.Generator;

internal static class MessagingDiagnostics
{
    /// <summary>
    /// Fired when a type crosses a process boundary (Target/Group subscription or
    /// ServerShared/ClientPersistent cache tier) without <c>[MemoryPackable]</c>.
    /// </summary>
    public static readonly DiagnosticDescriptor YFRPC0001 = new(
        id:                 "YFRPC0001",
        title:              "Cross-boundary type requires [MemoryPackable]",
        messageFormat:      "'{0}' crosses a process boundary but is not marked [MemoryPackable]. " +
                            "Add [MemoryPackable] to the type to enable serialization for RPC transport.",
        category:           "YFex.Messaging.Rpc",
        defaultSeverity:    DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:        "Types used with [Subscribe<T>(Target=...)] / [Subscribe<T>(Group=...)] " +
                            "or [Live(Cache = ServerShared|ClientPersistent)] must be MemoryPack-serializable.");

    /// <summary>
    /// Fired when a [Live] method has no explicit DependsOn and no polling configured,
    /// meaning the property will only refresh when RefreshXAsync() is called manually.
    /// </summary>
    public static readonly DiagnosticDescriptor YFLIV0002 = new(
        id:                 "YFLIV0002",
        title:              "[Live] property has no automatic refresh trigger",
        messageFormat:      "'{0}' has no DependsOn and PollMs = 0. The generated property " +
                            "will only refresh when RefreshXAsync() is called explicitly. " +
                            "Add DependsOn = [nameof(YourProperty)] or PollMs to enable auto-refresh.",
        category:           "YFex.Messaging",
        defaultSeverity:    DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description:        "Add DependsOn to wire [Observable] property changes as refresh triggers, " +
                            "or set PollMs for interval-based refresh.");

    /// <summary>
    /// Fired when a [Live] method returns a type that is not Task&lt;T&gt; or ValueTask&lt;T&gt;.
    /// </summary>
    public static readonly DiagnosticDescriptor YFLIV0001 = new(
        id:                 "YFLIV0001",
        title:              "[Live] method must return Task<T> or ValueTask<T>",
        messageFormat:      "'{0}' is decorated with [Live] but its return type is not Task<T> or ValueTask<T>. " +
                            "Change the return type to Task<T> or ValueTask<T>.",
        category:           "YFex.Messaging",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // ── YFSUB: [Subscribe<T>] structural diagnostics ──────────────────────────

    /// <summary>
    /// Class containing [Subscribe&lt;T&gt;] methods must be declared partial so the
    /// generator can emit its companion subscription wiring.
    /// </summary>
    public static readonly DiagnosticDescriptor YFSUB001 = new(
        id:                 "YFSUB001",
        title:              "[Subscribe<T>] class must be partial",
        messageFormat:      "'{0}' contains [Subscribe<T>] methods but is not declared 'partial'. " +
                            "Add the 'partial' modifier so the source generator can emit subscription wiring.",
        category:           "YFex.Messaging",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// Class containing [Subscribe&lt;T&gt;] methods must inherit StateObject or MessagingHost.
    /// The generator hooks into their activation lifecycles.
    /// </summary>
    public static readonly DiagnosticDescriptor YFSUB002 = new(
        id:                 "YFSUB002",
        title:              "[Subscribe<T>] class must inherit StateObject or MessagingHost",
        messageFormat:      "'{0}' contains [Subscribe<T>] methods but does not inherit " +
                            "'YFex.State.StateObject' or 'YFex.Messaging.MessagingHost'. " +
                            "The generator can only wire subscriptions into these base classes.",
        category:           "YFex.Messaging",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>
    /// [Subscribe&lt;T&gt;] cannot be applied to static methods — the generator captures
    /// 'this' inside the emitted adapter class.
    /// </summary>
    public static readonly DiagnosticDescriptor YFSUB003 = new(
        id:                 "YFSUB003",
        title:              "[Subscribe<T>] cannot be on a static method",
        messageFormat:      "'{0}' is static but is decorated with [Subscribe<T>]. " +
                            "Remove 'static' — the generated adapter captures 'this'.",
        category:           "YFex.Messaging",
        defaultSeverity:    DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
