using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Serializes an outgoing outbox command payload. When a source-generated
/// <see cref="JsonSerializerContext"/> covering the command type is supplied, serialization goes through
/// its <c>JsonTypeInfo</c> and is fully AOT / trim safe (no IL2026 / IL3050) — the same source-generated
/// approach the replay side (<see cref="OutboxCommandRegistry"/>) uses. When no context (or no matching
/// type-info) is available, a reflection-based fallback is used for convenience on non-AOT hosts; that
/// fallback is the single, explicitly-annotated escape hatch, so AOT consumers that supply a context
/// stay reflection-free end to end.
/// </summary>
internal static class OutboxCommandPayload
{
    public static byte[] Serialize<T>(T command, JsonSerializerContext? commandJsonContext)
        where T : ICommand
    {
        if (commandJsonContext?.GetTypeInfo(typeof(T)) is { } typeInfo)
            return JsonSerializer.SerializeToUtf8Bytes(command, typeInfo); // (object?, JsonTypeInfo) — AOT-safe

        return SerializeViaReflection(command);
    }

    // Non-AOT convenience fallback for hosts that did not register a source-generated command context.
    // Isolated + suppressed so it does not taint the AOT-clean path; supply a JsonSerializerContext to
    // avoid it entirely.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Opt-in non-AOT fallback. Supply a source-generated JsonSerializerContext for AOT.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Opt-in non-AOT fallback. Supply a source-generated JsonSerializerContext for AOT.")]
    private static byte[] SerializeViaReflection<T>(T command) where T : ICommand
        => JsonSerializer.SerializeToUtf8Bytes(command, typeof(T));
}
