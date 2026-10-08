using System.Collections.Concurrent;
using ActualLab.Rpc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using YFex.Cqrs;

namespace YFex.Messaging.Rpc;

/// <summary>RPC header names for at-most-once command execution (offline design F3, docs/internal/offline/08 §2).</summary>
public static class IdempotencyHeaders
{
    /// <summary>
    /// Idempotency key of a command call. The client creates one per command dispatch; an outbox replay re-sends the key the
    /// entry was queued with, so a replay after a lost reply does not execute the command twice.
    /// </summary>
    public const string Key = "yfex-idempotency";
}

/// <summary>
/// Ambient outbox entry being replayed (set by <see cref="OutboxReplayer"/> around the dispatch). <see cref="FusionMessageBus"/>
/// reads it to send the entry's idempotency key and the scope captured at enqueue time instead of fresh/current ones.
/// </summary>
internal static class OutboxReplayContext
{
    private static readonly AsyncLocal<OutboxEntry?> CurrentLocal = new();

    public static OutboxEntry? Current => CurrentLocal.Value;

    public static Scope Enter(OutboxEntry entry)
    {
        var previous = CurrentLocal.Value;
        CurrentLocal.Value = entry;
        return new Scope(previous);
    }

    public readonly struct Scope(OutboxEntry? previous) : IDisposable
    {
        public void Dispose() => CurrentLocal.Value = previous;
    }
}

/// <summary>What <see cref="IIdempotencyStore.ClaimAsync"/> found for a key.</summary>
public enum IdempotencyClaimKind : byte
{
    /// <summary>First time: the caller executes the command, then calls <see cref="IIdempotencyStore.CompleteAsync"/> or
    /// <see cref="IIdempotencyStore.AbandonAsync"/>.</summary>
    Acquired = 0,

    /// <summary>Already executed successfully: <see cref="IdempotencyClaim.Result"/> is the stored result.</summary>
    AlreadyCompleted = 1,

    /// <summary>Another call with the same key is executing: await <see cref="IdempotencyClaim.Pending"/>.</summary>
    InProgress = 2,
}

/// <summary>Result of <see cref="IIdempotencyStore.ClaimAsync"/>.</summary>
public readonly record struct IdempotencyClaim(IdempotencyClaimKind Kind, object? Result = null, Task<object?>? Pending = null);

/// <summary>
/// Server-side record of executed commands by idempotency key. Only <b>successful</b> executions are kept: a failed or rejected
/// call is abandoned, so a later retry with the same key executes again.
/// </summary>
public interface IIdempotencyStore
{
    ValueTask<IdempotencyClaim> ClaimAsync(string key, CancellationToken cancellationToken);
    ValueTask CompleteAsync(string key, object? result, CancellationToken cancellationToken);
    ValueTask AbandonAsync(string key, Exception reason, CancellationToken cancellationToken);
}

/// <summary>
/// In-process <see cref="IIdempotencyStore"/>: results are kept for <see cref="Retention"/> (default 24 h). Enough for one server
/// process; farms and restarts need a shared store (SQL, later — offline design 08 §3 keeps keys ≥ 30 days).
/// </summary>
public sealed class InMemoryIdempotencyStore(TimeProvider? clock = null) : IIdempotencyStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _lastSweepTicks;

    /// <summary>How long a completed result is remembered.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Remembered keys (completed or in progress).</summary>
    public int Count => _entries.Count;

    public ValueTask<IdempotencyClaim> ClaimAsync(string key, CancellationToken cancellationToken)
    {
        SweepIfDue();
        var fresh = new Entry();
        var entry = _entries.GetOrAdd(key, fresh);
        if (ReferenceEquals(entry, fresh)) return ValueTask.FromResult(new IdempotencyClaim(IdempotencyClaimKind.Acquired));
        if (entry.Completion.Task.IsCompletedSuccessfully)
            return ValueTask.FromResult(new IdempotencyClaim(IdempotencyClaimKind.AlreadyCompleted, entry.Completion.Task.Result));
        return ValueTask.FromResult(new IdempotencyClaim(IdempotencyClaimKind.InProgress, Pending: entry.Completion.Task));
    }

    public ValueTask CompleteAsync(string key, object? result, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.CompletedAtTicks = _clock.GetUtcNow().UtcTicks;
            entry.Completion.TrySetResult(result);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask AbandonAsync(string key, Exception reason, CancellationToken cancellationToken)
    {
        if (_entries.TryRemove(key, out var entry)) entry.Completion.TrySetException(reason);
        return ValueTask.CompletedTask;
    }

    private void SweepIfDue()
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref _lastSweepTicks, now, last) != last) return;
        var oldest = now - Retention.Ticks;
        foreach (var pair in _entries)
            if (pair.Value.CompletedAtTicks is > 0 and var completed && completed < oldest)
                _entries.TryRemove(pair);
    }

    private sealed class Entry
    {
        public readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long CompletedAtTicks;
    }
}

/// <summary>
/// Server-side at-most-once execution of commands (offline design F3). <see cref="ValidatingHandlerInvoker"/> runs it
/// <b>before</b> validation, rules and authorization, so a duplicate returns the stored result of the first successful
/// execution even if those stages would judge it differently now (the command already happened).
/// </summary>
/// <remarks>
/// The key is the command's configured business key (<c>.IdempotencyKey(...)</c> in the aggregate configuration) when there is
/// one, else the <see cref="IdempotencyHeaders.Key"/> header of the RPC call. It is bound to the message type and to the call's
/// scope header, so a key reused in another tenant or for another command never returns a foreign result. Calls without a key
/// (queries, in-process dispatch, old clients) run unchanged.
/// </remarks>
public sealed class IdempotentExecution(IIdempotencyStore store, CompiledMessagingRegistry registry)
{
    private static readonly RpcHeaderKey KeyHeader = new(IdempotencyHeaders.Key);
    private static readonly RpcHeaderKey ScopeHeader = new(CallContextHeaders.Scope);

    /// <summary>Runs <paramref name="execute"/> at most once per key; duplicates get the stored result.</summary>
    public async Task<T> RunAsync<T>(object message, Func<object, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        if (KeyFor(message) is not { } key) return await execute(message, ct).ConfigureAwait(false);

        var claim = await store.ClaimAsync(key, ct).ConfigureAwait(false);
        switch (claim.Kind)
        {
            case IdempotencyClaimKind.AlreadyCompleted:
                return claim.Result is T stored ? stored : default!;
            case IdempotencyClaimKind.InProgress:
                return await claim.Pending!.ConfigureAwait(false) is T pending ? pending : default!;
        }

        T result;
        try
        {
            result = await execute(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await store.AbandonAsync(key, ex, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await store.CompleteAsync(key, result, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    /// <summary>Runs <paramref name="execute"/> (no result) at most once per key.</summary>
    public async Task RunAsync(object message, Func<object, CancellationToken, Task> execute, CancellationToken ct)
    {
        if (KeyFor(message) is not { } key)
        {
            await execute(message, ct).ConfigureAwait(false);
            return;
        }

        var claim = await store.ClaimAsync(key, ct).ConfigureAwait(false);
        switch (claim.Kind)
        {
            case IdempotencyClaimKind.AlreadyCompleted:
                return;
            case IdempotencyClaimKind.InProgress:
                await claim.Pending!.ConfigureAwait(false);
                return;
        }

        try
        {
            await execute(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await store.AbandonAsync(key, ex, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await store.CompleteAsync(key, null, CancellationToken.None).ConfigureAwait(false);
    }

    private string? KeyFor(object message)
    {
        var type = message.GetType();
        if (!registry.Commands.TryGetValue(type, out var policy)) return null;   // queries are naturally repeatable

        var headers = RpcInboundContext.Current?.Message.Headers;
        var key = policy.IdempotencyKey is { } business ? business(message) : headers.TryGet(KeyHeader);
        if (string.IsNullOrEmpty(key)) return null;

        var scope = headers.TryGet(ScopeHeader) ?? "";
        return string.Concat(scope, "|", type.FullName, "|", key);
    }
}
