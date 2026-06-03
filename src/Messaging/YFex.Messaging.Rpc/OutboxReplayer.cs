using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YFex.Cqrs;
using YFex.Messaging;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Hosted service that drains the offline outbox whenever connectivity is restored.
/// Uses exponential backoff per-entry (max 5 min). Entries older than
/// <see cref="YFexMessagingRpcClientOptions.OutboxEntryTtl"/> are moved to
/// <see cref="ISyncFailureLog"/> with reason "expired".
/// </summary>
public sealed class OutboxReplayer : BackgroundService
{
    private readonly IOutbox _outbox;
    private readonly IWritableSyncFailureLog _failureLog;
    private readonly IDispatcher _dispatcher;
    private readonly INetworkStatus _networkStatus;
    private readonly SyncStatus _syncStatus;
    private readonly IEventBus _eventBus;
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
        YFexMessagingRpcClientOptions options,
        ILogger<OutboxReplayer> logger)
    {
        _outbox = outbox;
        _failureLog = failureLog;
        _dispatcher = dispatcher;
        _networkStatus = networkStatus;
        _syncStatus = syncStatus;
        _eventBus = eventBus;
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

        try
        {
            await ReplayAsync(entry, ct).ConfigureAwait(false);
            await _outbox.RemoveAsync(entry.IdempotencyKey, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandReplayedEvent(entry.IdempotencyKey, entry.CommandTypeName, true, null));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var delay = BackoffDelay(entry.AttemptCount);
            _logger.LogWarning(ex, "Replay failed for {Key} (attempt {N}), retrying in {S}s.",
                entry.IdempotencyKey, entry.AttemptCount + 1, delay.TotalSeconds);
            await _outbox.MarkAttemptedAsync(entry.IdempotencyKey, ex.Message, ct).ConfigureAwait(false);
            _eventBus.Publish(new CommandReplayedEvent(entry.IdempotencyKey, entry.CommandTypeName, false, ex.Message));
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private async Task MoveToFailureLogAsync(OutboxEntry entry, string reason, CancellationToken ct)
    {
        await _outbox.RemoveAsync(entry.IdempotencyKey, ct).ConfigureAwait(false);
        var failure = new SyncFailure(entry.IdempotencyKey, entry.CommandTypeName, entry.Payload, reason,
            DateTimeOffset.UtcNow, false);
        await _failureLog.AddAsync(failure, ct).ConfigureAwait(false);
        _eventBus.Publish(new SyncFailureEvent(entry.IdempotencyKey, entry.CommandTypeName, reason));
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Replay resolves command types by name.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Replay invokes generic CommandAsync via reflection.")]
    private async Task ReplayAsync(OutboxEntry entry, CancellationToken ct)
    {
        var commandType = Type.GetType(entry.CommandTypeName)
            ?? throw new InvalidOperationException($"Cannot resolve command type '{entry.CommandTypeName}'.");

        var command = JsonSerializer.Deserialize(entry.Payload, commandType)
            ?? throw new InvalidOperationException($"Failed to deserialize '{entry.CommandTypeName}'.");

        // CommandAsync<TCommand>(cmd, ct) — call via reflection since we don't have TCommand at compile time.
        // This is the recovery path, not the hot dispatch path.
        var method = typeof(IDispatcher).GetMethod(nameof(IDispatcher.CommandAsync), 1, [commandType, typeof(CancellationToken)])
            ?? throw new InvalidOperationException($"IDispatcher.CommandAsync not found for {commandType}.");

        var genericMethod = method.MakeGenericMethod(commandType);
        var task = (ValueTask<QueueableResult>)genericMethod.Invoke(_dispatcher, [command, ct])!;
        await task.ConfigureAwait(false);
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
