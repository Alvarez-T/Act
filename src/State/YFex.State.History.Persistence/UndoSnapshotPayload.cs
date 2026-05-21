using MemoryPack;

namespace YFex.State.History.Persistence;

/// <summary>
/// Top-level MemoryPack container for one <see cref="UndoContext"/> snapshot.
/// Contains the undo and redo stacks separately so they can be restored in order.
/// </summary>
[MemoryPackable]
public partial record UndoSnapshotPayload
{
    /// <summary>Undo stack, oldest first.</summary>
    public SerializedUndoGroup[] UndoGroups { get; init; } = [];

    /// <summary>Redo stack, oldest first.</summary>
    public SerializedUndoGroup[] RedoGroups { get; init; } = [];
}
