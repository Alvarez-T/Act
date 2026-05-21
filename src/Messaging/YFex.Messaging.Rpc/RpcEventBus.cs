using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using YFex.Messaging;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Composite <see cref="IEventBus"/> that routes events both locally (in-process)
/// and remotely (via <see cref="IRemoteEventBus"/> RPC service).
/// <para>
/// Publishing rules:
/// <list type="bullet">
///   <item>All events are delivered locally first (broadcast to in-process subscribers).</item>
///   <item>Events with <see cref="PublishOptions.TargetId"/> or <see cref="PublishOptions.GroupId"/>
///         are additionally forwarded to the server so other connected clients can receive them.</item>
///   <item>Events received from the server via <see cref="IRemoteEventBus.SubscribeToServerEventsAsync"/>
///         are deserialized and injected into the local bus.</item>
/// </list>
/// </para>
/// <para>
/// <b>AOT / trimming:</b> call <see cref="RegisterEventType{T}"/> at startup for every event type
/// that may arrive from the server. These pre-registrations are fully AOT-compatible and bypass the
/// reflection-based fallback. <see cref="StartServerListener"/> itself does not require unsafe code
/// when all incoming types are pre-registered.
/// </para>
/// </summary>
public sealed class RpcEventBus : IEventBus, IAsyncDisposable
{
    private readonly IEventBus _local;
    private readonly IRemoteEventBus _remote;

    // ── Dispatch registries ───────────────────────────────────────────────────
    // Primary: string-keyed, populated by RegisterEventType<T>() — fully AOT-safe.
    private readonly ConcurrentDictionary<string, Func<byte[], PublishOptions, ValueTask>> _namedDispatchers = new();

    // Fallback: type-keyed, populated lazily by BuildDispatcher() — requires reflection.
    private readonly ConcurrentDictionary<Type, Func<byte[], PublishOptions, ValueTask>> _reflectionDispatchers = new();

    private CancellationTokenSource? _serverListenCts;
    private Task? _serverListenTask;

    public RpcEventBus(IEventBus local, IRemoteEventBus remote)
    {
        _local  = local;
        _remote = remote;
    }

    // ── IEventBus passthrough ─────────────────────────────────────────────────

    public IDisposable Subscribe<T>(IEventRecipient<T> recipient, SubscribeOptions options = default)
        => _local.Subscribe(recipient, options);

    public IDisposable SubscribeAsync<T>(IAsyncEventRecipient<T> recipient, SubscribeOptions options = default)
        => _local.SubscribeAsync(recipient, options);

    // ── Publish: local + optionally remote ────────────────────────────────────

    public void Publish<T>(in T @event, PublishOptions options = default)
    {
        _local.Publish(in @event, options);
        if (options.TargetId is not null || options.GroupId is not null)
            _ = ForwardToRemoteAsync(@event, options, CancellationToken.None);
    }

    public async ValueTask PublishAsync<T>(T @event, PublishOptions options = default, CancellationToken ct = default)
    {
        await _local.PublishAsync(@event, options, ct).ConfigureAwait(false);
        if (options.TargetId is not null || options.GroupId is not null)
            await ForwardToRemoteAsync(@event, options, ct).ConfigureAwait(false);
    }

    // ── Remote forwarding ─────────────────────────────────────────────────────

    private async Task ForwardToRemoteAsync<T>(T @event, PublishOptions options, CancellationToken ct)
    {
        byte[] payload = MemoryPackSerializer.Serialize(@event);
        var envelope = new RpcEventEnvelope
        {
            TypeName = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name,
            Payload  = payload,
            TargetId = options.TargetId,
            GroupId  = options.GroupId,
        };
        await _remote.PublishAsync(envelope, ct).ConfigureAwait(false);
    }

    // ── AOT-safe pre-registration ─────────────────────────────────────────────

    /// <summary>
    /// Pre-registers an event type for AOT-safe server-push dispatch.
    /// Call once at startup for every event type that may arrive over the server-push stream.
    /// Pre-registered types bypass the reflection-based fallback entirely.
    /// </summary>
    public void RegisterEventType<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        string typeName = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;
        _namedDispatchers.TryAdd(typeName, (payload, opts) =>
        {
            var evt = MemoryPackSerializer.Deserialize<T>(payload);
            if (evt is null) return ValueTask.CompletedTask;
            return _local.PublishAsync(evt, opts);
        });
    }

    // ── Server-push listener ──────────────────────────────────────────────────

    /// <summary>
    /// Starts listening to the server-push stream. Events received from the server are
    /// deserialized and injected into the local bus.
    /// <para>
    /// For AOT deployments: ensure all expected event types are pre-registered via
    /// <see cref="RegisterEventType{T}"/> before calling this method. The reflection-based
    /// fallback is only reached for un-registered types.
    /// </para>
    /// </summary>
    public void StartServerListener()
    {
        _serverListenCts  = new CancellationTokenSource();
        _serverListenTask = ListenToServerAsync(_serverListenCts.Token);
    }

    private async Task ListenToServerAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var envelope in _remote.SubscribeToServerEventsAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                await DispatchEnvelopeAsync(envelope, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async ValueTask DispatchEnvelopeAsync(RpcEventEnvelope envelope, CancellationToken ct)
    {
        var opts = new PublishOptions { TargetId = envelope.TargetId, GroupId = envelope.GroupId };

        // Fast path: pre-registered AOT-safe dispatcher (no reflection)
        if (_namedDispatchers.TryGetValue(envelope.TypeName, out var dispatcher))
        {
            await dispatcher(envelope.Payload, opts).ConfigureAwait(false);
            return;
        }

        // Slow path: reflection-based fallback for un-registered types
        await DispatchViaReflectionAsync(envelope, opts, ct).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("Resolves event type by name at runtime. Pre-register with RegisterEventType<T>() for AOT.")]
    [RequiresDynamicCode("Uses MakeGenericMethod. Pre-register with RegisterEventType<T>() for AOT.")]
    private async ValueTask DispatchViaReflectionAsync(RpcEventEnvelope envelope, PublishOptions opts, CancellationToken ct)
    {
        Type? type = Type.GetType(envelope.TypeName);
        if (type is null) return;

        // Reflection cost paid once per type; delegate cached for subsequent calls.
        var reflectionDispatcher = _reflectionDispatchers.GetOrAdd(type, BuildReflectionDispatcher);
        await reflectionDispatcher(envelope.Payload, opts).ConfigureAwait(false);
    }

    [RequiresUnreferencedCode("Reflection-based dispatch")]
    [RequiresDynamicCode("Uses MakeGenericMethod")]
    private Func<byte[], PublishOptions, ValueTask> BuildReflectionDispatcher(Type eventType)
    {
        var method = typeof(RpcEventBus)
            .GetMethod(nameof(PublishObjectAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .MakeGenericMethod(eventType);

        return (payload, opts) =>
        {
            object? boxed = MemoryPackSerializer.Deserialize(eventType, payload);
            if (boxed is null) return ValueTask.CompletedTask;
            return (ValueTask)method.Invoke(this, [boxed, opts, CancellationToken.None])!;
        };
    }

    private ValueTask PublishObjectAsync<T>(object @event, PublishOptions opts, CancellationToken ct)
        => _local.PublishAsync((T)@event, opts, ct);

    // ── Disposal ──────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_serverListenCts is not null)
        {
            await _serverListenCts.CancelAsync().ConfigureAwait(false);
            if (_serverListenTask is not null)
                await _serverListenTask.ConfigureAwait(false);
            _serverListenCts.Dispose();
        }
    }
}
