namespace YFex.Diagnostics;

/// <summary>W3C trace flags plus one local bit.</summary>
[Flags]
public enum TraceFlags : byte
{
    None = 0,

    /// <summary>W3C <c>sampled</c> bit: the caller recorded this trace.</summary>
    Sampled = 1,

    /// <summary>Local only (never written to <c>traceparent</c>): the flight recorder is on for this trace.</summary>
    Recording = 2,
}

/// <summary>
/// Position in a trace: the trace, the current operation (W3C span id) and its parent. Flows ambiently with async code and is
/// carried across process boundaries as a W3C <c>traceparent</c> header.
/// </summary>
public readonly record struct TraceContext(TraceId TraceId, OperationId OperationId, OperationId ParentOperationId, TraceFlags Flags)
{
    /// <summary>Length of a version-00 <c>traceparent</c>: <c>00-{32 hex}-{16 hex}-{2 hex}</c>.</summary>
    public const int TraceParentLength = 55;

    /// <summary>True for <c>default</c> (no trace: code outside any operation).</summary>
    public bool IsEmpty => TraceId.IsEmpty;

    /// <summary>
    /// Writes <c>00-{trace}-{operation}-{flags}</c> (55 characters). Only the W3C <c>sampled</c> bit is written. Returns the number
    /// of characters written, or 0 when the context is empty or <paramref name="destination"/> is too small.
    /// </summary>
    public int WriteTraceParent(Span<char> destination)
    {
        if (IsEmpty || OperationId.IsEmpty || destination.Length < TraceParentLength) return 0;
        destination[0] = '0';
        destination[1] = '0';
        destination[2] = '-';
        TraceId.FormatHex(destination[3..]);
        destination[35] = '-';
        OperationId.FormatHex(destination[36..]);
        destination[52] = '-';
        Hex.Write((byte)(Flags & TraceFlags.Sampled), destination[53..]);
        return TraceParentLength;
    }

    /// <summary>
    /// Parses a W3C <c>traceparent</c>. The incoming span id becomes <see cref="ParentOperationId"/> of the returned context
    /// (whose own <see cref="OperationId"/> is empty: the receiver opens its own operation under it).
    /// Rules: lower-case hex only, version <c>ff</c> rejected, version <c>00</c> must be exactly 55 characters, later versions
    /// may append <c>-…</c> fields (ignored), all-zero trace or span ids rejected.
    /// </summary>
    public static bool TryParseTraceParent(ReadOnlySpan<char> text, out TraceContext context)
    {
        context = default;
        text = text.Trim();
        if (text.Length < TraceParentLength) return false;
        if (!Hex.TryRead(text[..2], lowerCaseOnly: true, out ulong version) || version == 0xFF) return false;
        if (version == 0 && text.Length != TraceParentLength) return false;
        if (text.Length > TraceParentLength && text[TraceParentLength] != '-') return false;
        if (text[2] != '-' || text[35] != '-' || text[52] != '-') return false;

        if (!Hex.TryRead(text.Slice(3, 16), true, out ulong high) || !Hex.TryRead(text.Slice(19, 16), true, out ulong low)) return false;
        if (!Hex.TryRead(text.Slice(36, 16), true, out ulong span)) return false;
        if (!Hex.TryRead(text.Slice(53, 2), true, out ulong flags)) return false;
        if ((high | low) == 0 || span == 0) return false;

        context = new TraceContext(new TraceId(high, low), default, new OperationId(span), (TraceFlags)((byte)flags & (byte)TraceFlags.Sampled));
        return true;
    }

    /// <summary>A child position: same trace, new operation id, this operation as parent.</summary>
    public TraceContext CreateChild() => new(TraceId, OperationId.NewRandom(), OperationId, Flags);

    /// <summary>A new root position: new trace, new operation, no parent.</summary>
    public static TraceContext NewRoot(TraceFlags flags = TraceFlags.None) => new(TraceId.NewRandom(), OperationId.NewRandom(), default, flags);
}

/// <summary>Why an operation is linked to another one that is not its parent (logging 05 §4).</summary>
public enum LinkKind : byte
{
    /// <summary>Caused by, but not part of (scheduled job, published event, background work).</summary>
    FollowsFrom = 0,

    /// <summary>Offline outbox replay or event-sourcing catch-up of the linked operation.</summary>
    Replay = 1,

    /// <summary>Batch fan-in: one of many inputs.</summary>
    Batch = 2,

    /// <summary>Retry of the linked operation in a later process.</summary>
    Retry = 3,
}

/// <summary>A causal link that is not a parent: offline replay, event-sourcing catch-up, batch fan-in, cross-process retry.</summary>
public readonly record struct OperationLink(TraceId TraceId, OperationId OperationId, LinkKind Kind)
{
    /// <summary>True when there is nothing to link to.</summary>
    public bool IsEmpty => TraceId.IsEmpty;

    /// <summary>A link to the ambient operation (<see cref="Operations.Current"/>); empty when there is none.</summary>
    public static OperationLink FromCurrent(LinkKind kind)
    {
        TraceContext current = Operations.Current;
        return current.IsEmpty ? default : new OperationLink(current.TraceId, current.OperationId, kind);
    }

    /// <summary>A link to a stored context (for example the one saved with an outbox entry).</summary>
    public static OperationLink To(in TraceContext context, LinkKind kind)
        => context.IsEmpty ? default : new OperationLink(context.TraceId, context.OperationId, kind);
}
