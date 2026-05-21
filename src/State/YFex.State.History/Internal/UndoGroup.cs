using System;

namespace YFex.State.History.Internal;

/// <summary>
/// An atomic undo unit: one or more deltas that are undone/redone as a single step.
/// Doubly-linked when on the undo stack (for O(1) oldest-entry eviction).
/// Forms an object pool to avoid re-allocation when history entries are evicted.
/// </summary>
internal sealed class UndoGroup
{
    // ── Undo stack doubly-linked list ──────────────────────────────────────────
    internal UndoGroup? PreviousUndo;   // towards older entries (tail direction)
    internal UndoGroup? NextUndo;       // towards newer entries (head direction)

    // ── Redo stack singly-linked list ─────────────────────────────────────────
    internal UndoGroup? PreviousRedo;

    // ── Delta chain within this group ─────────────────────────────────────────
    internal UndoDeltaBase? First;
    internal UndoDeltaBase? Last;
    internal int Count;

    // ── Metadata ──────────────────────────────────────────────────────────────
    internal string? Label;
    internal DateTime Timestamp;

    // ── Object pool link ──────────────────────────────────────────────────────
    internal UndoGroup? PoolNext;

    internal void AppendDelta(UndoDeltaBase delta)
    {
        if (First is null) { First = Last = delta; }
        else { Last!.Next = delta; Last = delta; }
        Count++;
    }

    internal void Reset()
    {
        PreviousUndo = NextUndo = PreviousRedo = PoolNext = null;
        var node = First;
        while (node is not null)
        {
            var next = node.Next;
            node.Next = null;
            node = next;
        }
        First = Last = null;
        Count = 0;
        Label = null;
    }

    /// <summary>Builds the auto-description shown in the history panel.</summary>
    internal string BuildDescription()
    {
        if (Label is not null) return Label;
        if (Count == 0) return "No changes";
        if (Count == 1) return $"Changed {First!.PropertyName}";
        return $"Changed {Count} properties";
    }
}
