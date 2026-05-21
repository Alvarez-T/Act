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
}
