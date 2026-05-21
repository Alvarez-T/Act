using YFex.State.Collections;

namespace YFex.State.History.Internal;

/// <summary>
/// Records a full before/after snapshot of a <see cref="StateList{T}"/> for collection undo/redo.
/// Uses a shadow copy maintained by <see cref="UndoableCollectionObserver{T}"/> to capture
/// the pre-mutation state, since <see cref="StateList{T}"/> only fires post-change notifications.
/// Undo restores via <c>Clear</c> + <c>AddRange</c>; redo re-applies the after-snapshot the same way.
/// </summary>
internal sealed class CollectionUndoDelta<T> : UndoDeltaBase
{
    private readonly StateList<T> _list;
    private readonly T[] _beforeItems;
    private readonly T[] _afterItems;
    private readonly string _propertyName;

    internal CollectionUndoDelta(
        StateList<T> list,
        T[] beforeItems,
        T[] afterItems,
        string propertyName)
    {
        _list         = list;
        _beforeItems  = beforeItems;
        _afterItems   = afterItems;
        _propertyName = propertyName;
    }

    internal override string PropertyName => _propertyName;

    internal override void ApplyUndo() => Restore(_beforeItems);
    internal override void ApplyRedo() => Restore(_afterItems);

    private void Restore(T[] items)
    {
        _list.Clear();
        _list.AddRange(items.AsSpan());
    }
}
