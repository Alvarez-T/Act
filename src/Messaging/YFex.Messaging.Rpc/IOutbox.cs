using MemoryPack;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Cqrs;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>A command awaiting replay in the offline outbox.</summary>
/// <param name="IdempotencyKey">Created once at enqueue; every replay attempt sends it (<see cref="IdempotencyHeaders.Key"/>) so the
/// server executes the command at most once (offline design F3).</param>
/// <param name="Subject">Who queued it (<see cref="IOutboxCallerCapture"/>); <see langword="null"/> for anonymous callers and for entries
/// written before offline Phase 0.</param>
/// <param name="Scope">Canonical tenant scope at enqueue time. Replays send this scope, not the one active at replay time (offline
/// design F4).</param>
/// <param name="Sequence">Monotonic per outbox, persisted. Replay order is by sequence, never by wall clock (offline design F5);
/// 0 = written before offline Phase 0 (ordered by <see cref="EnqueuedAt"/>, ahead of numbered entries).</param>
[MemoryPackable]
public sealed partial record OutboxEntry(
    Guid IdempotencyKey,
    string CommandTypeName,
    byte[] Payload,
    DateTimeOffset EnqueuedAt,
    int AttemptCount,
    string? LastFailure,
    DateTimeOffset? LastAttemptAt,
    string? Subject = null,
    string? Scope = null,
    long Sequence = 0)
{
    /// <summary>Replay order: sequence first, then enqueue time for legacy entries (sequence 0).</summary>
    public static int CompareReplayOrder(OutboxEntry left, OutboxEntry right)
    {
        var bySequence = left.Sequence.CompareTo(right.Sequence);
        return bySequence != 0 ? bySequence : left.EnqueuedAt.CompareTo(right.EnqueuedAt);
    }
}

/// <summary>Constrains the maximum size of the offline outbox.</summary>
public sealed class OutboxOptions
{
    /// <summary>Maximum number of pending entries. Oldest entries overflow to <see cref="ISyncFailureLog"/> with reason "outbox-overflow".</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>Optional cap by total serialized size in bytes.</summary>
    public long? MaxStorageBytes { get; set; }

    /// <summary>
    /// What happens when an entry outlives <see cref="YFexMessagingRpcClientOptions.OutboxEntryTtl"/>. <see langword="true"/>
    /// (default): a warning is logged once and the entry keeps being replayed — a queued sale already happened and must not
    /// silently become a dead letter (offline design F5/F13). <see langword="false"/>: the entry moves to
    /// <see cref="ISyncFailureLog"/> with reason "expired" (the behaviour before offline Phase 0).
    /// </summary>
    public bool KeepExpiredEntries { get; set; } = true;
}

/// <summary>
/// Client-side offline command queue. Commands implementing <see cref="IQueueable"/> are
/// enqueued here when disconnected and drained by <see cref="OutboxReplayer"/> on reconnect.
/// </summary>
public interface IOutbox
{
    ValueTask<Queued> EnqueueAsync<T>(T command, CancellationToken ct = default) where T : ICommand;
    ValueTask<IReadOnlyList<OutboxEntry>> ListPendingAsync(CancellationToken ct = default);
    ValueTask MarkAttemptedAsync(Guid key, string? failure, CancellationToken ct = default);
    ValueTask RemoveAsync(Guid key, CancellationToken ct = default);
    int PendingCount { get; }
    event Action<OutboxEntry>? Enqueued;
    event Action<Guid>? Drained;
}

/// <summary>Who queued a command and in which tenant scope (captured at enqueue time).</summary>
public readonly record struct OutboxCaller(string? Subject, string? Scope);

/// <summary>
/// Captures the caller when a command is queued, so the replay does not borrow the identity or scope that happens to be active
/// when the connection returns (offline design F4, interim until the signed offline envelope of offline Phase 3).
/// </summary>
public interface IOutboxCallerCapture
{
    OutboxCaller Capture();
}

/// <summary>
/// Default capture: subject from <see cref="IPrincipalAccessor"/> (when registered and authenticated), scope from
/// <see cref="IScopeContext"/> (when registered).
/// </summary>
public sealed class DefaultOutboxCallerCapture(IServiceProvider services) : IOutboxCallerCapture
{
    private readonly IPrincipalAccessor? _principal = services.GetService<IPrincipalAccessor>();
    private readonly IScopeContext? _scope = services.GetService<IScopeContext>();

    public OutboxCaller Capture()
    {
        var caller = _principal?.Current;
        var subject = caller is { IsAuthenticated: true } ? caller.Actor.Subject : null;
        var scope = _scope.EffectiveIdentity() is { IsEmpty: false } identity ? identity.ToString() : null;
        return new OutboxCaller(subject, scope);
    }
}
