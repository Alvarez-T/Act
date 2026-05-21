using System;
using System.Collections.Generic;
using YFex.State.History;

namespace YFex.State.History.Testing;

/// <summary>
/// Fluent assertion extension methods for <see cref="UndoContext"/> in test code.
/// </summary>
public static class UndoContextAssertions
{
    /// <summary>Asserts that the context can currently undo.</summary>
    public static UndoContext ShouldBeAbleToUndo(this UndoContext ctx)
    {
        if (!ctx.CanUndo)
            throw new InvalidOperationException(
                $"Expected UndoContext to CanUndo, but CanUndo is false. UndoDepth={ctx.UndoDepth}");
        return ctx;
    }

    /// <summary>Asserts that the context cannot currently undo.</summary>
    public static UndoContext ShouldNotBeAbleToUndo(this UndoContext ctx)
    {
        if (ctx.CanUndo)
            throw new InvalidOperationException(
                $"Expected UndoContext to not CanUndo, but CanUndo is true. UndoDepth={ctx.UndoDepth}");
        return ctx;
    }

    /// <summary>Asserts that the context can currently redo.</summary>
    public static UndoContext ShouldBeAbleToRedo(this UndoContext ctx)
    {
        if (!ctx.CanRedo)
            throw new InvalidOperationException(
                $"Expected UndoContext to CanRedo, but CanRedo is false. RedoDepth={ctx.RedoDepth}");
        return ctx;
    }

    /// <summary>Asserts that the context cannot currently redo.</summary>
    public static UndoContext ShouldNotBeAbleToRedo(this UndoContext ctx)
    {
        if (ctx.CanRedo)
            throw new InvalidOperationException(
                $"Expected UndoContext to not CanRedo, but CanRedo is true. RedoDepth={ctx.RedoDepth}");
        return ctx;
    }

    /// <summary>Asserts that <see cref="UndoContext.UndoDepth"/> equals <paramref name="expected"/>.</summary>
    public static UndoContext ShouldHaveUndoDepth(this UndoContext ctx, int expected)
    {
        if (ctx.UndoDepth != expected)
            throw new InvalidOperationException(
                $"Expected UndoDepth={expected} but was {ctx.UndoDepth}.");
        return ctx;
    }

    /// <summary>Asserts that <see cref="UndoContext.RedoDepth"/> equals <paramref name="expected"/>.</summary>
    public static UndoContext ShouldHaveRedoDepth(this UndoContext ctx, int expected)
    {
        if (ctx.RedoDepth != expected)
            throw new InvalidOperationException(
                $"Expected RedoDepth={expected} but was {ctx.RedoDepth}.");
        return ctx;
    }

    /// <summary>Asserts that <see cref="UndoContext.IsAtSavepoint"/> is <see langword="true"/>.</summary>
    public static UndoContext ShouldBeAtSavepoint(this UndoContext ctx)
    {
        if (!ctx.IsAtSavepoint)
            throw new InvalidOperationException(
                $"Expected IsAtSavepoint=true but UndoDepth={ctx.UndoDepth}.");
        return ctx;
    }

    /// <summary>Asserts that <see cref="UndoContext.IsAtSavepoint"/> is <see langword="false"/>.</summary>
    public static UndoContext ShouldNotBeAtSavepoint(this UndoContext ctx)
    {
        if (ctx.IsAtSavepoint)
            throw new InvalidOperationException(
                "Expected IsAtSavepoint=false but context is currently at the savepoint.");
        return ctx;
    }

    /// <summary>
    /// Asserts the undo history count equals <paramref name="expected"/>.
    /// </summary>
    public static UndoContext ShouldHaveHistoryCount(this UndoContext ctx, int expected)
    {
        int actual = ctx.UndoHistory.Count;
        if (actual != expected)
            throw new InvalidOperationException(
                $"Expected UndoHistory.Count={expected} but was {actual}.");
        return ctx;
    }
}
