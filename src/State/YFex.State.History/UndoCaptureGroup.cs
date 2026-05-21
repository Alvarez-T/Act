using System;
using System.Collections.Generic;

namespace YFex.State.History;

/// <summary>
/// Serializable snapshot of one undo/redo group.
/// Used by the persistence layer to save and restore the undo stack.
/// </summary>
public sealed class UndoCaptureGroup
{
    /// <summary>User-supplied transaction label, or <see langword="null"/> for auto-generated.</summary>
    public string? Label { get; }

    /// <summary>Timestamp of the most recent change in the group.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Property-change deltas that make up this group. Collection deltas are excluded.</summary>
    public IReadOnlyList<UndoCaptureDelta> Deltas { get; }

    public UndoCaptureGroup(string? label, DateTime timestamp, IReadOnlyList<UndoCaptureDelta> deltas)
    {
        Label     = label;
        Timestamp = timestamp;
        Deltas    = deltas;
    }
}
