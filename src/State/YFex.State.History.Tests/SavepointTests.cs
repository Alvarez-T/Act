using YFex.State.History;

namespace YFex.State.History.Tests;

/// <summary>Tests for UndoContext savepoint / dirty-tracking integration.</summary>
public sealed class SavepointTests
{
    private static readonly Action<object, object?> s_noop = static (_, _) => { };

    private static void Record(UndoContext ctx, string old = "a", string @new = "b")
        => ctx.RecordPropertyChange(new object(), "Prop", s_noop, old, @new,
            DateTime.UtcNow.Ticks, 0);

    [Fact]
    public void NoSavepoint_IsAtSavepoint_False()
        => new UndoContext().IsAtSavepoint.Should().BeFalse();

    [Fact]
    public void SetSavepoint_AtCurrentDepth_IsAtSavepoint_True()
    {
        var ctx = new UndoContext();
        Record(ctx);
        ctx.SetSavepoint();
        ctx.IsAtSavepoint.Should().BeTrue();
    }

    [Fact]
    public void ChangeAfterSavepoint_IsAtSavepoint_False()
    {
        var ctx = new UndoContext();
        ctx.SetSavepoint();
        Record(ctx);
        ctx.IsAtSavepoint.Should().BeFalse();
    }

    [Fact]
    public void UndoBackToSavepoint_IsAtSavepoint_True()
    {
        var ctx = new UndoContext();
        ctx.SetSavepoint(); // depth=0 as savepoint
        Record(ctx);        // depth=1
        ctx.Undo();         // back to depth=0
        ctx.IsAtSavepoint.Should().BeTrue();
    }

    [Fact]
    public void SavepointChangedEvent_Fires_OnTransition()
    {
        var ctx          = new UndoContext();
        int eventCount   = 0;
        ctx.SavepointChanged += () => eventCount++;

        ctx.SetSavepoint();      // at savepoint (depth=0) — no transition yet, still false→true? No.
        Record(ctx);             // dirty — fires SavepointChanged: true→false
        ctx.Undo();              // clean — fires SavepointChanged: false→true

        eventCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ClearSavepoint_IsAtSavepoint_False()
    {
        var ctx = new UndoContext();
        ctx.SetSavepoint();
        ctx.IsAtSavepoint.Should().BeTrue();

        ctx.ClearSavepoint();
        ctx.IsAtSavepoint.Should().BeFalse();
    }
}
