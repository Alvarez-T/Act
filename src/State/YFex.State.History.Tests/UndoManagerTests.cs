using YFex.State.History;

namespace YFex.State.History.Tests;

/// <summary>
/// Tests for <see cref="UndoManager"/> cross-context coordination.
/// </summary>
public sealed class UndoManagerTests
{
    private static readonly Action<object, object?> s_noop = static (_, _) => { };

    private static void Record(UndoContext ctx, string name, string old, string @new)
        => ctx.RecordPropertyChange(new object(), name, s_noop, old, @new,
            DateTime.UtcNow.Ticks, 0);

    [Fact]
    public void Empty_CannotUndoOrRedo()
    {
        var mgr = new UndoManager();
        mgr.ShouldNotBeAbleToUndo();
        mgr.ShouldNotBeAbleToRedo();
    }

    [Fact]
    public void RegisterContext_WithEntry_CanUndo()
    {
        var mgr = new UndoManager();
        var ctx = new UndoContext();
        Record(ctx, "A", "1", "2");

        mgr.Register(ctx);
        mgr.ShouldBeAbleToUndo();
    }

    [Fact]
    public void Undo_DelegatestoMostRecentContext()
    {
        var mgr = new UndoManager();
        var ctx = new UndoContext();
        Record(ctx, "Name", "old", "new");

        mgr.Register(ctx);
        mgr.Undo();

        ctx.UndoDepth.Should().Be(0);
        ctx.RedoDepth.Should().Be(1);
    }

    [Fact]
    public void TwoContexts_UndoPicksMostRecent()
    {
        var mgr  = new UndoManager();
        var ctx1 = new UndoContext();
        var ctx2 = new UndoContext();

        mgr.Register(ctx1);
        mgr.Register(ctx2);

        Record(ctx1, "P1", "a", "b");
        System.Threading.Thread.Sleep(5); // ensure ctx2 timestamp is newer
        Record(ctx2, "P2", "x", "y");

        mgr.Undo();

        // ctx2 (more recent) should be undone
        ctx2.UndoDepth.Should().Be(0);
        ctx1.UndoDepth.Should().Be(1); // untouched
    }

    [Fact]
    public void Unregister_RemovesContext()
    {
        var mgr = new UndoManager();
        var ctx = new UndoContext();
        Record(ctx, "X", "1", "2");

        mgr.Register(ctx);
        mgr.Unregister(ctx);
        mgr.ShouldNotBeAbleToUndo();
    }

    [Fact]
    public void SetSavepoint_AppliedToAllContexts()
    {
        var mgr  = new UndoManager();
        var ctx1 = new UndoContext();
        var ctx2 = new UndoContext();
        mgr.Register(ctx1);
        mgr.Register(ctx2);

        mgr.SetSavepoint();
        mgr.IsAtSavepoint.Should().BeTrue();
        ctx1.IsAtSavepoint.Should().BeTrue();
        ctx2.IsAtSavepoint.Should().BeTrue();
    }

    [Fact]
    public void SetSavepoint_ThenOneContextChanges_IsAtSavepoint_False()
    {
        var mgr  = new UndoManager();
        var ctx1 = new UndoContext();
        var ctx2 = new UndoContext();
        mgr.Register(ctx1);
        mgr.Register(ctx2);

        mgr.SetSavepoint();
        Record(ctx1, "X", "a", "b"); // ctx1 now dirty
        mgr.IsAtSavepoint.Should().BeFalse();
    }

    [Fact]
    public void UndoCommand_CanExecute_ReflectsState()
    {
        var mgr = new UndoManager();
        var ctx = new UndoContext();
        mgr.Register(ctx);

        mgr.UndoCommand.CanExecute().Should().BeFalse();
        Record(ctx, "A", "1", "2");
        mgr.UndoCommand.CanExecute().Should().BeTrue();
    }
}
