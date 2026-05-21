using YFex.State.Commands;

namespace YFex.State.History;

/// <summary>
/// Implemented by objects that expose a single primary <see cref="UndoContext"/>.
/// The generator emits this interface when <c>[Undoable]</c> is applied at class level
/// or when the class has exactly one undo scope. For classes with multiple scopes, implement
/// this interface manually by choosing the primary context.
/// </summary>
public interface IUndoable
{
    /// <summary>The primary undo/redo context for this object.</summary>
    UndoContext UndoHistory { get; }

    /// <summary>Command that performs one undo step on <see cref="UndoHistory"/>.</summary>
    IStateCommand UndoCommand { get; }

    /// <summary>Command that performs one redo step on <see cref="UndoHistory"/>.</summary>
    IStateCommand RedoCommand { get; }
}
