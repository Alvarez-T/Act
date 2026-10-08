using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using YFex.Persistence;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The client's outbox and its dead-letter log, created together and cross-wired (overflow → failure log, retry → outbox).
/// </summary>
/// <remarks>
/// Offline design F14: before offline Phase 0 the client always used the in-memory pair, so queued commands were lost on restart
/// even when a durable <see cref="IKeyValueStore"/> (SQLite, IndexedDB) was registered. The durable pair is chosen when
/// <see cref="YFexMessagingRpcClientOptions.Storage"/> is not <see cref="ClientStorageBackend.InMemory"/> and the registered store
/// is not <see cref="MemoryKeyValueStore"/>.
/// </remarks>
internal sealed class OutboxPair(IOutbox outbox, IWritableSyncFailureLog failureLog)
{
    public IOutbox Outbox { get; } = outbox;
    public IWritableSyncFailureLog FailureLog { get; } = failureLog;

    public static OutboxPair Create(IServiceProvider services, YFexMessagingRpcClientOptions options)
    {
        var storage = services.GetRequiredService<IKeyValueStore>();
        var commandJsonContext = services.GetService<JsonSerializerContext>();
        var callerCapture = services.GetService<IOutboxCallerCapture>();

        if (options.Storage != ClientStorageBackend.InMemory && storage is not MemoryKeyValueStore)
        {
            var durableOutbox = new StorageBackedOutbox(storage, options.OutboxOptions, commandJsonContext, callerCapture);
            var durableLog = new StorageBackedSyncFailureLog(storage);
            durableOutbox.SetFailureLog(durableLog);
            durableLog.SetOutbox(durableOutbox);
            return new OutboxPair(durableOutbox, durableLog);
        }

        var memoryOutbox = new InMemoryOutbox(options.OutboxOptions, commandJsonContext, callerCapture);
        var memoryLog = new InMemorySyncFailureLog();
        memoryOutbox.SetFailureLog(memoryLog);
        memoryLog.SetOutbox(memoryOutbox);
        return new OutboxPair(memoryOutbox, memoryLog);
    }
}
