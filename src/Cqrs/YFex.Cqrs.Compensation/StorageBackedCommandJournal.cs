using System.Globalization;
using System.Text.Json;
using MemoryPack;
using YFex.Persistence;

namespace YFex.Cqrs.Compensation;

/// <summary>
/// <see cref="ICommandJournal"/> over any <see cref="IKeyValueStore"/>, cloning the
/// <c>StorageBackedOutbox</c> approach: one entry per key under the <c>journal:</c> prefix, a write
/// lock for serialized appends, and a lazily-scanned sequence counter. Because it rides on
/// <see cref="IKeyValueStore"/>, it inherits SQLite + IndexedDB + file-system backends for free.
///
/// Keys are <c>journal:{seq:D19}</c> — zero-padded so lexicographic key order (as returned by
/// <see cref="IKeyValueStore.GetKeysWithPrefixAsync"/>) matches numeric sequence order, making the
/// newest entry poppable in LIFO order without materializing every entry.
/// </summary>
public sealed class StorageBackedCommandJournal : ICommandJournal
{
    private const string Prefix = "journal:";
    private const string SeqFormat = "D19";

    private readonly IKeyValueStore _storage;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _nextSeq = -1; // -1 = needs a cold scan

    public StorageBackedCommandJournal(IKeyValueStore storage) => _storage = storage;

    private static string Key(long seq) => Prefix + seq.ToString(SeqFormat, CultureInfo.InvariantCulture);

    public async ValueTask<long> AppendAsync(
        object command,
        object? result,
        Type? reverseCommandType,
        string? principal,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var commandType = command.GetType();

        var entry = new CommandJournalEntry(
            Seq: 0, // assigned under the lock below
            CommandTypeName: commandType.AssemblyQualifiedName ?? commandType.FullName!,
            Command: JsonSerializer.SerializeToUtf8Bytes(command, commandType),
            ResultTypeName: result?.GetType() is { } rt ? rt.AssemblyQualifiedName ?? rt.FullName : null,
            Result: result is null ? null : JsonSerializer.SerializeToUtf8Bytes(result, result.GetType()),
            ReverseCommandTypeName: reverseCommandType?.AssemblyQualifiedName ?? reverseCommandType?.FullName,
            Principal: principal,
            Utc: DateTimeOffset.UtcNow,
            Reversed: false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = await NextSeqAsync(ct).ConfigureAwait(false);
            var toStore = entry with { Seq = seq };
            await _storage.SetAsync(Key(seq), MemoryPackSerializer.Serialize(toStore), ct: ct).ConfigureAwait(false);
            return seq;
        }
        finally { _writeLock.Release(); }
    }

    public async ValueTask<CommandJournalEntry?> TryPopNewestUnreversedAsync(CancellationToken ct = default)
    {
        var keys = await _storage.GetKeysWithPrefixAsync(Prefix, ct).ConfigureAwait(false);
        if (keys.Count == 0) return null;

        // Descending ordinal order == descending sequence order thanks to D19 padding.
        var ordered = new List<string>(keys);
        ordered.Sort(static (a, b) => string.CompareOrdinal(b, a));

        for (int i = 0; i < ordered.Count; i++)
        {
            var bytes = await _storage.GetAsync(ordered[i], ct).ConfigureAwait(false);
            if (bytes is null) continue;
            var entry = MemoryPackSerializer.Deserialize<CommandJournalEntry>(bytes);
            if (entry is not null && !entry.Reversed) return entry;
        }
        return null;
    }

    public async ValueTask MarkReversedAsync(long seq, CancellationToken ct = default)
    {
        var key = Key(seq);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var bytes = await _storage.GetAsync(key, ct).ConfigureAwait(false);
            if (bytes is null) return;
            var entry = MemoryPackSerializer.Deserialize<CommandJournalEntry>(bytes);
            if (entry is null || entry.Reversed) return; // idempotency guard
            await _storage.SetAsync(key, MemoryPackSerializer.Serialize(entry with { Reversed = true }), ct: ct)
                .ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    // Must be called under _writeLock. Lazily scans existing keys once to resume the counter after
    // a restart, then increments in-memory.
    private async ValueTask<long> NextSeqAsync(CancellationToken ct)
    {
        if (_nextSeq < 0)
        {
            long max = 0;
            var keys = await _storage.GetKeysWithPrefixAsync(Prefix, ct).ConfigureAwait(false);
            for (int i = 0; i < keys.Count; i++)
            {
                var raw = keys[i].AsSpan(Prefix.Length);
                if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v > max)
                    max = v;
            }
            _nextSeq = max + 1;
        }
        return _nextSeq++;
    }
}
