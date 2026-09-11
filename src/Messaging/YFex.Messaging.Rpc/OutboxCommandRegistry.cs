using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Deserializes a serialized outbox <c>Payload</c> to its concrete command type, dispatches it
/// through the typed <see cref="IDispatcher.CommandAsync{TCommand}(TCommand, CancellationToken)"/>
/// overload, and — when the server <b>rejects</b> the command (a failed
/// <see cref="QueueableResult"/> carrying an <see cref="Error"/>) — resolves the command's
/// conflict policy. Populated by source generation — never resolves types by name or via
/// <c>MakeGenericMethod</c>, so it is fully AOT / trim safe.
/// <para>
/// A <b>transient</b> failure (a dropped connection etc.) surfaces as a thrown exception and is
/// left to the caller's retry/backoff path; only a returned <see cref="ReplayOutcome"/> with
/// <see cref="ReplayOutcome.IsRejected"/> represents a permanent, business-level rejection.
/// </para>
/// </summary>
public delegate ValueTask<ReplayOutcome> OutboxReplayHandler(
    byte[] payload, IDispatcher dispatcher, IServiceProvider services, CancellationToken ct);

/// <summary>
/// Result of a single outbox replay attempt as observed by <see cref="OutboxReplayer"/>.
/// A replay either succeeds, or is <b>rejected</b> by the server with a resolved
/// <see cref="ConflictPolicy"/>. Transient (network) failures never produce a rejected outcome —
/// they throw and are retried.
/// </summary>
public readonly struct ReplayOutcome
{
    /// <summary>True when the server rejected the command (permanent, business-level conflict).</summary>
    public bool IsRejected { get; }

    /// <summary>The resolved conflict policy — valid only when <see cref="IsRejected"/> is true.</summary>
    public ConflictPolicy Policy { get; }

    /// <summary>The server error that triggered the rejection — valid only when <see cref="IsRejected"/> is true.</summary>
    public Error Error { get; }

    /// <summary>The rejected command's CLR type (for looking up its invalidation targets). Null on success.</summary>
    public Type? CommandType { get; }

    /// <summary>The materialized command instance (used to compute per-entity invalidation tags). Null on success.</summary>
    public object? Command { get; }

    private ReplayOutcome(bool rejected, ConflictPolicy policy, Error error, Type? commandType, object? command)
    {
        IsRejected = rejected;
        Policy = policy;
        Error = error;
        CommandType = commandType;
        Command = command;
    }

    /// <summary>The command replayed successfully (server accepted it).</summary>
    public static ReplayOutcome Success { get; } = new(false, default, default, null, null);

    /// <summary>The command was rejected; act on <paramref name="policy"/>.</summary>
    public static ReplayOutcome Rejected(ConflictPolicy policy, Error error, Type commandType, object command)
        => new(true, policy, error, commandType, command);
}

/// <summary>
/// Reflection-free conflict-policy resolution for a rejected replay. Called from the
/// source-generated per-command replay delegate where <c>TCommand</c> is a compile-time generic
/// parameter, so a custom resolver is resolved via the strongly-typed
/// <c>GetService&lt;IConflictResolver&lt;TCommand&gt;&gt;()</c> (no <c>MakeGenericType</c>).
/// </summary>
internal static class OutboxConflictResolution
{
    /// <summary>
    /// Resolves the effective <see cref="ConflictPolicy"/> for a rejected command: a
    /// <see cref="IConflictResolver{TCommand}"/> registered in DI wins; otherwise the static
    /// <see cref="CommandPolicy.Conflict"/> configured for the command type applies; failing both,
    /// the command is kept for later retry (<see cref="ConflictPolicy.RetryLater"/>).
    /// </summary>
    public static async ValueTask<ReplayOutcome> RejectAsync<TCommand>(
        TCommand command, Error error, IServiceProvider services, CancellationToken ct)
        where TCommand : class, ICommand
    {
        ConflictPolicy policy;

        var resolver = services.GetService<IConflictResolver<TCommand>>();
        if (resolver is not null)
        {
            policy = await resolver.ResolveAsync(command, error, ct).ConfigureAwait(false);
        }
        else
        {
            var registry = services.GetService<CompiledMessagingRegistry>();
            policy = registry is not null && registry.Commands.TryGetValue(typeof(TCommand), out var cmdPolicy)
                ? cmdPolicy.Conflict
                : ConflictPolicy.RetryLater;
        }

        return ReplayOutcome.Rejected(policy, error, typeof(TCommand), command);
    }
}

/// <summary>
/// Compile-time replacement for the old reflection-based outbox replay path
/// (<c>Type.GetType</c> + <c>MakeGenericMethod</c>). Maps a stable command type name to a
/// generated, strongly-typed materialize+dispatch delegate.
/// <para>
/// Built by the source-generated <c>YFexOutboxCommandRegistrations.AddYFexOutboxCommands()</c>
/// (one entry per command type discovered in the consuming assembly). Consumers targeting AOT can
/// build it manually via <see cref="OutboxCommandRegistryBuilder"/> supplying source-generated
/// <see cref="JsonTypeInfo{T}"/> for zero-reflection deserialization.
/// </para>
/// </summary>
public sealed class OutboxCommandRegistry
{
    private readonly FrozenDictionary<string, OutboxReplayHandler> _handlers;

    /// <summary>An empty registry (no command types registered). Used as the DI fallback.</summary>
    public static OutboxCommandRegistry Empty { get; } =
        new(new Dictionary<string, OutboxReplayHandler>(0));

    internal OutboxCommandRegistry(IReadOnlyDictionary<string, OutboxReplayHandler> handlers)
        => _handlers = handlers.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Looks up the generated replay handler for a serialized command's type name
    /// (matches both <c>AssemblyQualifiedName</c> and <c>FullName</c>).
    /// </summary>
    public bool TryGetHandler(string commandTypeName, [NotNullWhen(true)] out OutboxReplayHandler? handler)
        => _handlers.TryGetValue(commandTypeName, out handler);

    /// <summary>Number of registered handler keys.</summary>
    public int Count => _handlers.Count;
}

/// <summary>
/// Accumulates typed command replay handlers, then freezes them into an
/// <see cref="OutboxCommandRegistry"/>. Each command is keyed by both
/// <see cref="Type.AssemblyQualifiedName"/> and <see cref="Type.FullName"/> so the lookup matches
/// whatever the outbox stored at enqueue time.
/// </summary>
public sealed class OutboxCommandRegistryBuilder
{
    private readonly Dictionary<string, OutboxReplayHandler> _handlers = new(StringComparer.Ordinal);

    // ── Void commands (ICommand) ──────────────────────────────────────────────

    /// <summary>
    /// Registers a void command (<see cref="ICommand"/>) using a source-generated
    /// <see cref="JsonTypeInfo{T}"/>. Fully AOT / trim safe: no reflection-based JSON, no dynamic
    /// code, no <c>MakeGenericMethod</c>. Dispatches via the void
    /// <see cref="IDispatcher.CommandAsync{TCommand}(TCommand, CancellationToken)"/> overload.
    /// </summary>
    public OutboxCommandRegistryBuilder Add<T>(JsonTypeInfo<T> typeInfo) where T : class, ICommand
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Register<T>(async (payload, dispatcher, services, ct) =>
        {
            var cmd = Deserialize(payload, typeInfo);
            var result = await dispatcher.CommandAsync<T>(cmd, ct).ConfigureAwait(false);
            return result.IsError && result.TryGetError(out var error)
                ? await OutboxConflictResolution.RejectAsync(cmd, error, services, ct).ConfigureAwait(false)
                : ReplayOutcome.Success;
        });
        return this;
    }

    /// <summary>
    /// Registers a void command using reflection-based JSON deserialization. Convenient for non-AOT
    /// hosts; for AOT use <see cref="Add{T}(JsonTypeInfo{T})"/>. Dispatch is always reflection-free.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON deserialization. Use Add<T>(JsonTypeInfo<T>) for AOT.")]
    [RequiresDynamicCode("Reflection-based JSON deserialization. Use Add<T>(JsonTypeInfo<T>) for AOT.")]
    public OutboxCommandRegistryBuilder Add<T>() where T : class, ICommand
    {
        Register<T>(async (payload, dispatcher, services, ct) =>
        {
            var cmd = Deserialize<T>(payload);
            var result = await dispatcher.CommandAsync<T>(cmd, ct).ConfigureAwait(false);
            return result.IsError && result.TryGetError(out var error)
                ? await OutboxConflictResolution.RejectAsync(cmd, error, services, ct).ConfigureAwait(false)
                : ReplayOutcome.Success;
        });
        return this;
    }

    // ── Result-bearing commands (ICommand&lt;TResult&gt;) ───────────────────────

    /// <summary>
    /// Registers a result-bearing command (<see cref="ICommand{TResult}"/>) using a source-generated
    /// <see cref="JsonTypeInfo{T}"/>. Fully AOT / trim safe. Dispatches via the typed
    /// <see cref="IDispatcher.CommandAsync{TCommand, TResult}(TCommand, CancellationToken)"/> overload
    /// so the correct handler is resolved.
    /// </summary>
    public OutboxCommandRegistryBuilder Add<T, TResult>(JsonTypeInfo<T> typeInfo) where T : class, ICommand<TResult>
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Register<T>(async (payload, dispatcher, services, ct) =>
        {
            var cmd = Deserialize(payload, typeInfo);
            var result = await dispatcher.CommandAsync<T, TResult>(cmd, ct).ConfigureAwait(false);
            return result.IsError && result.TryGetError(out var error)
                ? await OutboxConflictResolution.RejectAsync(cmd, error, services, ct).ConfigureAwait(false)
                : ReplayOutcome.Success;
        });
        return this;
    }

    /// <summary>
    /// Registers a result-bearing command using reflection-based JSON deserialization. Convenient for
    /// non-AOT hosts; for AOT use <see cref="Add{T, TResult}(JsonTypeInfo{T})"/>.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON deserialization. Use Add<T, TResult>(JsonTypeInfo<T>) for AOT.")]
    [RequiresDynamicCode("Reflection-based JSON deserialization. Use Add<T, TResult>(JsonTypeInfo<T>) for AOT.")]
    public OutboxCommandRegistryBuilder Add<T, TResult>() where T : class, ICommand<TResult>
    {
        Register<T>(async (payload, dispatcher, services, ct) =>
        {
            var cmd = Deserialize<T>(payload);
            var result = await dispatcher.CommandAsync<T, TResult>(cmd, ct).ConfigureAwait(false);
            return result.IsError && result.TryGetError(out var error)
                ? await OutboxConflictResolution.RejectAsync(cmd, error, services, ct).ConfigureAwait(false)
                : ReplayOutcome.Success;
        });
        return this;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static T Deserialize<T>(byte[] payload, JsonTypeInfo<T> typeInfo) where T : class
        => JsonSerializer.Deserialize(payload, typeInfo)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize outbox payload for command '{typeof(T)}'.");

    [RequiresUnreferencedCode("Reflection-based JSON deserialization.")]
    [RequiresDynamicCode("Reflection-based JSON deserialization.")]
    private static T Deserialize<T>(byte[] payload) where T : class
        => JsonSerializer.Deserialize<T>(payload)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize outbox payload for command '{typeof(T)}'.");

    private void Register<T>(OutboxReplayHandler handler)
    {
        if (typeof(T).AssemblyQualifiedName is { } aqn) _handlers[aqn] = handler;
        if (typeof(T).FullName is { } full) _handlers[full] = handler;
    }

    /// <summary>Freezes the accumulated handlers into an immutable registry.</summary>
    public OutboxCommandRegistry Build() => new(_handlers);
}
