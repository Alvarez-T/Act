using System;
using YFex.State.History;

namespace YFex.State.History.Testing;

/// <summary>
/// Fluent assertion extension methods for <see cref="UndoManager"/> in test code.
/// </summary>
public static class UndoManagerAssertions
{
    /// <summary>Asserts that the manager can currently undo across any registered context.</summary>
    public static UndoManager ShouldBeAbleToUndo(this UndoManager mgr)
    {
        if (!mgr.CanUndo)
            throw new InvalidOperationException(
                "Expected UndoManager to CanUndo, but no registered context has undo entries.");
        return mgr;
    }

    /// <summary>Asserts that the manager cannot currently undo.</summary>
    public static UndoManager ShouldNotBeAbleToUndo(this UndoManager mgr)
    {
        if (mgr.CanUndo)
            throw new InvalidOperationException(
                "Expected UndoManager to not CanUndo, but at least one context has undo entries.");
        return mgr;
    }

    /// <summary>Asserts that the manager can currently redo.</summary>
    public static UndoManager ShouldBeAbleToRedo(this UndoManager mgr)
    {
        if (!mgr.CanRedo)
            throw new InvalidOperationException(
                "Expected UndoManager to CanRedo, but no context has redo entries.");
        return mgr;
    }

    /// <summary>Asserts that the manager cannot currently redo.</summary>
    public static UndoManager ShouldNotBeAbleToRedo(this UndoManager mgr)
    {
        if (mgr.CanRedo)
            throw new InvalidOperationException(
                "Expected UndoManager to not CanRedo.");
        return mgr;
    }

    /// <summary>Asserts that all registered contexts are at their savepoints.</summary>
    public static UndoManager ShouldBeAtSavepoint(this UndoManager mgr)
    {
        if (!mgr.IsAtSavepoint)
            throw new InvalidOperationException(
                "Expected UndoManager.IsAtSavepoint=true but one or more contexts are not at their savepoints.");
        return mgr;
    }
}
