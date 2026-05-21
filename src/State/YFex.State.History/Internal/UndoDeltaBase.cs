namespace YFex.State.History.Internal;

/// <summary>
/// Abstract base for all undo delta types (property and collection).
/// Forms an intrusive singly-linked list within an <see cref="UndoGroup"/>.
/// </summary>
internal abstract class UndoDeltaBase
{
    internal UndoDeltaBase? Next;

    /// <summary>Property name for history description generation.</summary>
    internal abstract string PropertyName { get; }

    /// <summary>Reverts the change represented by this delta.</summary>
    internal abstract void ApplyUndo();

    /// <summary>Re-applies the change (used by redo).</summary>
    internal abstract void ApplyRedo();

    /// <summary>
    /// Returns true if this delta can absorb <paramref name="incoming"/> as a coalescing update
    /// (same owner, same property, within the merge window).
    /// </summary>
    internal virtual bool CanMergeWith(UndoDeltaBase incoming) => false;

    /// <summary>Updates this delta to absorb <paramref name="incoming"/>.</summary>
    internal virtual void MergeWith(UndoDeltaBase incoming) { }
}
