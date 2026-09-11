using MemoryPack;

namespace YFex.Cqrs.Compensation;

/// <summary>
/// One durable record of an executed reversible command. Serialized with MemoryPack and stored
/// under the ordered key <c>journal:{Seq:D19}</c> so the newest entry is lexicographically last
/// (LIFO undo). The command/result payloads are opaque JSON blobs — the reverse command is
/// materialized from the compiled <c>ReversePolicy</c>, not from these bytes' shape.
/// </summary>
[MemoryPackable]
public sealed partial record CommandJournalEntry(
    /// <summary>Monotonic sequence number. Higher = newer.</summary>
    long Seq,
    /// <summary>Assembly-qualified name of the original command type.</summary>
    string CommandTypeName,
    /// <summary>JSON-serialized original command.</summary>
    byte[] Command,
    /// <summary>Assembly-qualified name of the result type, or null for void commands / no result.</summary>
    string? ResultTypeName,
    /// <summary>JSON-serialized result, or null.</summary>
    byte[]? Result,
    /// <summary>Assembly-qualified name of the declared reverse command type ("reverseDescriptor-ref"),
    /// or null when reversal is via a custom handler.</summary>
    string? ReverseCommandTypeName,
    /// <summary>Principal that executed the command, if captured.</summary>
    string? Principal,
    /// <summary>UTC timestamp of execution.</summary>
    DateTimeOffset Utc,
    /// <summary>True once the reverse has been dispatched (idempotency guard).</summary>
    bool Reversed);

/// <summary>
/// Append-only, LIFO-poppable journal of executed reversible commands, backed by any
/// <see cref="YFex.Persistence.IKeyValueStore"/> (SQLite, IndexedDB, file system, in-memory).
/// </summary>
public interface ICommandJournal
{
    /// <summary>Serializes and appends a new entry; returns its assigned sequence number.</summary>
    ValueTask<long> AppendAsync(
        object command,
        object? result,
        Type? reverseCommandType,
        string? principal,
        CancellationToken ct = default);

    /// <summary>Returns the newest entry not yet marked reversed, or null when none remain.
    /// Does not mutate — call <see cref="MarkReversedAsync"/> after the reverse succeeds.</summary>
    ValueTask<CommandJournalEntry?> TryPopNewestUnreversedAsync(CancellationToken ct = default);

    /// <summary>Marks the entry with <paramref name="seq"/> as reversed. No-op when the entry is
    /// missing or already reversed (idempotent).</summary>
    ValueTask MarkReversedAsync(long seq, CancellationToken ct = default);
}
