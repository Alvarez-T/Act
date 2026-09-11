using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Messaging;
using YFex.Persistence;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Hosted service that drains the offline outbox whenever connectivity is restored.
/// Uses exponential backoff per-entry (max 5 min). Entries older than
/// <see cref="YFexMessagingRpcClientOptions.OutboxEntryTtl"/> are moved to
/// <see cref="ISyncFailureLog"/> with reason "expired".
/// <para>
/// A replayed command that the server <b>rejects</b> (a business-level conflict, surfaced as a
/// failed <see cref="QueueableResult"/> carrying an <see cref="Error"/> — not a transient network
/// exception) is resolved via the command's conflict policy
/// (<see cref="IConflictResolver{TCommand}"/> in DI, else the static
/// <see cref="CommandPolicy.Conflict"/>): <see cref="ConflictPolicy.RetryLater"/> keeps the
/// backoff/retry path; <see cref="ConflictPolicy.Discard"/> drops the entry; and
/// <see cref="ConflictPolicy.Escalate"/> dead-letters it to <see cref="ISyncFailureLog"/>.
/// Because the rejection means nothing was committed server-side, Discard/Escalate also <b>undo the
/// local optimistic effect</b> by invalidating the command's optimistically-updated caches so they
/// refetch authoritative state — <b>not</b> by dispatching a reverse command.
/// </para>
/// </summary>
public sealed class OutboxReplayer : BackgroundService
{
    private readonly IOutbox _outbox;
    private readonly IWritableSyncFailureLog _failureLog;
    private readonly IDispatcher _dispatcher;
    private readonly INetworkStatus _networkStatus;
    private readonly SyncStatus _syncStatus;
    private readonly IEventBus _eventBus;
    private readonly OutboxCommandRegistry _commandRegistry;
    private readonly ICache _cache;
    private readonly CompiledMessagingRegistry _messagingRegistry;
    private readonly IServiceProvider _services;
    private readonly TimeSpan _entryTtl;
    private readonly ILogger<OutboxReplayer> _logger;
    private readonly SemaphoreSlim _trigger = new(0, 1);

    public OutboxReplayer(
        IOutbox outbox,
        IWritableSyncFailureLog failureLog,
        IDispatcher dispatcher,
        INetworkStatus networkStatus,
        SyncStatus syncStatus,
        IEventBus eventBus,
        OutboxCommandRegistry commandRegistry,
        ICache cache,
        CompiledMessagingRegistry messagingRegistry,
        IServiceProvider services,
        YFexMessagingRpcClientOptions options,
        ILogger<OutboxReplayer> logger)
    {
        _outbox = outbox;
        _failureLog = failureLog;
        _dispatcher = dispatcher;
        _networkStatus = networkStatus;
        _syncStatus = syncStatus;
        _eventBus = eventBus;
        _commandRegistry = commandRegistry;
        _cache = cache;
        _messagingRegistry = messagingRegistry;
        _services = services;
        _entryTtl = options.OutboxEntryTtl;
        _logger = logger;

        _networkStatus.Changed += OnNetworkChanged;
        _outbox.Enqueued += OnEnqueued;
    }

    private void OnNetworkChanged(SyncState state)
    {
        _syncStatus.IsOffline = state != SyncState.Connected;
        _eventBus.Publish(new NetworkStatusChangedEvent(
            state == SyncState.Connected ? SyncState.Disconnected : SyncState.Connected, state));
        if (state == SyncState.Connected && _trigger.CurrentCount == 0)
            _trigger.Release();
    }

    private void OnEnqueued(OutboxEntry entry)
    {
        _syncStatus.PendingCommandCount = _outbox.PendingCount;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Attempt a drain on startup in case we reconnect before the first change event.
        if (_networkStatus.IsConnected)
            await DrainAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait up to 30 s between checks; a reconnect triggers immediately.
            await _trigger.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
            if (_networkStatus.IsConnected)
                await DrainAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        _syncStatus.IsSyncing = true;
        try
        {
            var entries = await _outbox.ListPendingAsync(ct).ConfigureAwait(false);
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessEntryAsync(entries[i], ct).ConfigureAwait(false);
            }
            _syncStatus.LastSyncAt = DateTimeOffset.UtcNow;
            _syncStatus.LastSyncError = null;
        }
        catch (OperationCanceledException) { /* host shutting down */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Outbox drain failed.");
            _syncStatus.LastSyncError = ex;
        }
        finally
        {
            _syncStatus.IsSyncing = false;
            _syncStatus.PendingCommandCount = _outbox.PendingCount;
        }
    }

    private async Task ProcessEntryAsync(OutboxEntry entry, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - entry.EnqueuedAt > _entryTtl)
        {
            await MoveToFailureLogAsync(entry, "expired", ct).ConfigureAwait(false);
            return;
        }

        ReplayOutcome outcome;
        try
        {
            outcome = await ReplayAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // TRANSIENT failure (connection drop / handler exception): keep the entry and back off.
            // This is NOT a business conflict — the conflict resolvers are never consulted here.
            await RetryLaterAsync(entry, ex.Message, ct).ConfigureAwait(false);
            return;
        }

        if (!outcome.IsRejected)
        {
            await _outbox.RemoveAsync(entry.IdempotencyKey, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandReplayedEvent(entry.IdempotencyKey, entry.CommandTypeName, true, null));
            return;
        }

        // SERVER REJECTION (permanent, business-level): resolve via the command's conflict policy.
        await HandleConflictAsync(entry, outcome, ct).ConfigureAwait(false);
    }

    private async Task HandleConflictAsync(OutboxEntry entry, ReplayOutcome outcome, CancellationToken ct)
    {
        var reason = ConflictReason(outcome);
        switch (outcome.Policy)
        {
            case ConflictPolicy.RetryLater:
                // Treat like a transient miss: keep the entry and retry with backoff.
                await RetryLaterAsync(entry, reason, ct).ConfigureAwait(false);
                break;

            case ConflictPolicy.Discard:
                // Drop the command AND undo the local optimistic effect (invalidate → refetch).
                _logger.LogInformation("Replay of {Key} rejected; discarding per conflict policy. {Reason}",
                    entry.IdempotencyKey, reason);
                await _outbox.RemoveAsync(entry.IdempotencyKey, ct).ConfigureAwait(false);
                await UndoOptimisticEffectAsync(outcome, ct).ConfigureAwait(false);
                _eventBus.Publish(new CommandReplayedEvent(entry.IdempotencyKey, entry.CommandTypeName, false, reason));
                break;

            case ConflictPolicy.Escalate:
            default:
                // Dead-letter the command AND undo the local optimistic effect (invalidate → refetch).
                _logger.LogWarning("Replay of {Key} rejected; escalating to the sync failure log. {Reason}",
                    entry.IdempotencyKey, reason);
                await MoveToFailureLogAsync(entry, "conflict", ct).ConfigureAwait(false);
                await UndoOptimisticEffectAsync(outcome, ct).ConfigureAwait(false);
                break;
        }
    }

    // Common backoff/retry path shared by transient failures and the RetryLater conflict policy.
    // Increments the attempt count (so the next backoff grows) and leaves the entry in the outbox.
    private async Task RetryLaterAsync(OutboxEntry entry, string reason, CancellationToken ct)
    {
        var delay = BackoffDelay(entry.AttemptCount);
        _logger.LogWarning("Replay failed for {Key} (attempt {N}), retrying in {S}s. {Reason}",
            entry.IdempotencyKey, entry.AttemptCount + 1, delay.TotalSeconds, reason);
        await _outbox.MarkAttemptedAsync(entry.IdempotencyKey, reason, ct).ConfigureAwait(false);
        _eventBus.Publish(new CommandReplayedEvent(entry.IdempotencyKey, entry.CommandTypeName, false, reason));
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    // Undo the optimistic effect of a rejected command: because the server rejected it, nothing was
    // committed server-side, so the correct rollback is to invalidate the caches the command
    // optimistically touched (its declared invalidation targets) and let them refetch authoritative
    // state — NOT to dispatch a reverse command (that is for undoing a command that SUCCEEDED).
    private async ValueTask UndoOptimisticEffectAsync(ReplayOutcome outcome, CancellationToken ct)
    {
        if (outcome.Command is null || outcome.CommandType is null) return;
        if (!_messagingRegistry.Commands.TryGetValue(outcome.CommandType, out var policy)) return;
        if (policy.InvalidationTargets is not { } targets) return;

        for (int i = 0; i < targets.Length; i++)
            await _cache.RemoveByTagAsync(QueryCacheKey.InvalidationTag(targets[i], outcome.Command), ct)
                .ConfigureAwait(false);
    }

    private static string ConflictReason(in ReplayOutcome outcome)
        => string.IsNullOrEmpty(outcome.Error.Message) ? "conflict" : outcome.Error.Message;

    private async Task MoveToFailureLogAsync(OutboxEntry entry, string reason, CancellationToken ct)
    {
        await _outbox.RemoveAsync(entry.IdempotencyKey, ct).ConfigureAwait(false);
        var failure = new SyncFailure(entry.IdempotencyKey, entry.CommandTypeName, entry.Payload, reason,
            DateTimeOffset.UtcNow, false);
        await _failureLog.AddAsync(failure, ct).ConfigureAwait(false);
        _eventBus.Publish(new SyncFailureEvent(entry.IdempotencyKey, entry.CommandTypeName, reason));
    }

    // Source-generated, reflection-free replay: the generated OutboxCommandRegistry maps the stored
    // command type name to a strongly-typed materialize+dispatch delegate (no Type.GetType, no
    // MakeGenericMethod). The delegate also resolves the conflict policy for a rejected command,
    // where the command type is a compile-time generic (no reflection). Populate it at startup via
    // the generated AddYFexOutboxCommands().
    private async ValueTask<ReplayOutcome> ReplayAsync(OutboxEntry entry, CancellationToken ct)
    {
        if (!_commandRegistry.TryGetHandler(entry.CommandTypeName, out var handler))
            throw new InvalidOperationException(
                $"No replay handler registered for command '{entry.CommandTypeName}'. " +
                "Ensure the generated AddYFexOutboxCommands() is called at startup so the command " +
                "type is included in the OutboxCommandRegistry.");

        return await handler(entry.Payload, _dispatcher, _services, ct).ConfigureAwait(false);
    }

    private static TimeSpan BackoffDelay(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 300));

    public override void Dispose()
    {
        _networkStatus.Changed -= OnNetworkChanged;
        _outbox.Enqueued -= OnEnqueued;
        _trigger.Dispose();
        base.Dispose();
    }
}
