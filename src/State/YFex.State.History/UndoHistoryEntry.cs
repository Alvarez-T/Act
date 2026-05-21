using System;

namespace YFex.State.History;

/// <summary>
/// Immutable snapshot of one undo/redo stack entry. Used for history visualization
/// (e.g., an undo history panel). Descriptions are auto-generated from property names
/// unless a label was supplied via <see cref="UndoContext.BeginTransaction(string?)"/>.
/// </summary>
public sealed class UndoHistoryEntry
{
    /// <summary>User-supplied label, or <see langword="null"/> if auto-generated.</summary>
    public string? Label { get; }

    /// <summary>
    /// Human-readable description: "Changed Name", "Changed 3 properties", etc.
    /// Equal to <see cref="Label"/> when a label was supplied.
    /// </summary>
    public string Description { get; }

    /// <summary>Timestamp of the most recent change in this group.</summary>
    public DateTime Timestamp { get; }

    /// <summary>Number of individual property/collection deltas in this group.</summary>
    public int DeltaCount { get; }

    internal UndoHistoryEntry(string? label, string description, DateTime timestamp, int deltaCount)
    {
        Label       = label;
        Description = description;
        Timestamp   = timestamp;
        DeltaCount  = deltaCount;
    }
}
