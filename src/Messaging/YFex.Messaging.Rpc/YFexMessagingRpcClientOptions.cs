namespace YFex.Messaging.Rpc;

/// <summary>
/// Which outbox the client uses. The storage itself is whatever <c>IKeyValueStore</c> the app registers (SQLite, IndexedDB…);
/// encrypt it with <c>AddYFexStorageEncryption</c> (YFex.Persistence.Encryption).
/// </summary>
public enum ClientStorageBackend
{
    /// <summary>Durable outbox when the registered store is durable (not <c>MemoryKeyValueStore</c>), else in-memory.</summary>
    Auto,
    /// <summary>Always the in-memory outbox (queued commands are lost on restart).</summary>
    InMemory,
    /// <summary>Same as <see cref="Auto"/>; documents intent.</summary>
    Sqlite,
    /// <summary>Same as <see cref="Auto"/>; documents intent.</summary>
    IndexedDb,
}

/// <summary>Options for <c>AddYFexMessagingRpcClient</c>.</summary>
public sealed class YFexMessagingRpcClientOptions
{
    /// <summary>WebSocket endpoint of the Fusion RPC server (e.g. <c>wss://api.myapp.com</c>).</summary>
    public Uri WebSocketEndpoint { get; set; } = new("ws://localhost:5000");

    /// <summary>
    /// Outbox selection (offline design F14): <see cref="ClientStorageBackend.Auto"/> uses the durable outbox over the registered
    /// store unless it is in-memory. Encryption at rest is configured on the store (<c>AddYFexStorageEncryption</c>); the
    /// former <c>Encryption</c>/<c>EncryptionKeySource</c> options here were never read and were removed (offline design F12).
    /// </summary>
    public ClientStorageBackend Storage { get; set; } = ClientStorageBackend.Auto;

    /// <summary>Never read: configure the storage backend's own location instead.</summary>
    [Obsolete("Not used by the RPC client; configure the storage backend (e.g. the SQLite connection) instead.")]
    public string? StorageDirectory { get; set; }

    public OutboxOptions OutboxOptions { get; set; } = new();

    /// <summary>
    /// Age after which an outbox entry is considered expired: by default it is kept and a warning is logged
    /// (<see cref="OutboxOptions.KeepExpiredEntries"/>); otherwise it moves to <see cref="ISyncFailureLog"/> with reason "expired".
    /// </summary>
    public TimeSpan OutboxEntryTtl { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Called after the client is fully configured — register AOT event types here.</summary>
    public Action<RpcEventBus>? ConfigureEventBus { get; set; }

    internal bool EnableServerPushedEventsFlag { get; private set; }

    /// <summary>Opt-in: activates <c>FusionEventStream</c> → <c>IEventBus</c> bridge on the client side.</summary>
    public YFexMessagingRpcClientOptions EnableServerPushedEvents()
    {
        EnableServerPushedEventsFlag = true;
        return this;
    }

    internal CallContextClientBuilder? CallContextBuilder { get; private set; }

    /// <summary>
    /// Call-context options (validation D29) — only for the per-call header option:
    /// <c>rpc.CallContext(ctx => ctx.SendToken((sp, ct) => tokens.GetAsync(ct)).SendTenant())</c>. The default
    /// (connection-bound identity) needs nothing on the client: the WebSocket upgrade request carries the credentials.
    /// </summary>
    public YFexMessagingRpcClientOptions CallContext(Action<CallContextClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(CallContextBuilder ??= new CallContextClientBuilder());
        return this;
    }
}

/// <summary>Options for <c>UseYFexMessagingRpcServer</c>.</summary>
public sealed class YFexMessagingRpcServerOptions
{
    public bool ConvertExceptionsToResults { get; set; }

    internal bool EnableServerPushedEventsFlag { get; private set; }

    /// <summary>Opt-in: registers <c>EventChannelHost</c> and the Wolverine event bridge.</summary>
    public YFexMessagingRpcServerOptions EnableServerPushedEvents()
    {
        EnableServerPushedEventsFlag = true;
        return this;
    }

    internal CallContextServerBuilder? CallContextBuilder { get; private set; }

    /// <summary>
    /// Where the caller's identity (+ tenant) of each RPC call comes from (validation D29):
    /// <c>rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader("Authorization").WithTenant())</c>.
    /// Not calling it = <c>ctx.FromConnection()</c> (principal captured at connect time, no tenant).
    /// </summary>
    public YFexMessagingRpcServerOptions CallContext(Action<CallContextServerBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(CallContextBuilder ??= new CallContextServerBuilder());
        return this;
    }
}
