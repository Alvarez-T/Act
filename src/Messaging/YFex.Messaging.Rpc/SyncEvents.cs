using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>Published to <see cref="YFex.Messaging.IEventBus"/> when a command is queued offline.</summary>
public sealed record CommandQueuedEvent(
    Guid IdempotencyKey,
    string CommandTypeName,
    DateTimeOffset QueuedAt) : IEvent;

/// <summary>Published when a queued command is replayed (succeeded or failed).</summary>
public sealed record CommandReplayedEvent(
    Guid IdempotencyKey,
    string CommandTypeName,
    bool Succeeded,
    string? ErrorMessage) : IEvent;

/// <summary>Published when a queued command is moved to <see cref="ISyncFailureLog"/> permanently.</summary>
public sealed record SyncFailureEvent(
    Guid IdempotencyKey,
    string CommandTypeName,
    string Reason) : IEvent;

/// <summary>Published when the device's network state transitions.</summary>
public sealed record NetworkStatusChangedEvent(
    SyncState Previous,
    SyncState Current) : IEvent;
