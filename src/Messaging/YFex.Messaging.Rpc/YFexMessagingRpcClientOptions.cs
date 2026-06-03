namespace YFex.Messaging.Rpc;

public enum ClientStorageBackend { Auto, InMemory, Sqlite, IndexedDb }
public enum EncryptionMode { None, AesGcm }
public enum EncryptionKeySource { OsKeyStore, Provided }

/// <summary>Options for <c>AddYFexMessagingRpcClient</c>.</summary>
public sealed class YFexMessagingRpcClientOptions
{
    /// <summary>WebSocket endpoint of the Fusion RPC server (e.g. <c>wss://api.myapp.com</c>).</summary>
    public Uri WebSocketEndpoint { get; set; } = new("ws://localhost:5000");

    public ClientStorageBackend Storage { get; set; } = ClientStorageBackend.Auto;

    /// <summary>Root directory for file-based backends (SQLite). Ignored for IndexedDB/InMemory.</summary>
    public string? StorageDirectory { get; set; }

    public EncryptionMode Encryption { get; set; } = EncryptionMode.None;
    public EncryptionKeySource EncryptionKeySource { get; set; } = EncryptionKeySource.OsKeyStore;

    public OutboxOptions OutboxOptions { get; set; } = new();

    /// <summary>Entries older than this TTL are moved to <see cref="ISyncFailureLog"/> with reason "expired".</summary>
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
}
