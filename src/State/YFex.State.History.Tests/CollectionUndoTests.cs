using YFex.State.Collections;
using YFex.State.History;

namespace YFex.State.History.Tests;

/// <summary>
/// Tests for <see cref="UndoableCollectionObserver{T}"/> and manual collection change recording.
/// Uses ListVm defined in TestViewModels.cs.
/// </summary>
public sealed class CollectionUndoTests
{
    [Fact]
    public void TrackCollection_Add_ThenUndo_RemovesItem()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>();
        using var observer = new UndoableCollectionObserver<string>(ctx, list, "Items");

        list.Add("hello");

        ctx.ShouldBeAbleToUndo();
        ctx.Undo();
        list.Count.Should().Be(0);
    }

    [Fact]
    public void TrackCollection_Remove_ThenUndo_RestoresItem()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>(initialCapacity: 4);
        list.Add("alpha");
        list.Add("beta");

        using var observer = new UndoableCollectionObserver<string>(ctx, list, "Items");

        list.RemoveAt(0); // remove "alpha"

        ctx.Undo();
        list.Count.Should().Be(2);
        list[0].Should().Be("alpha");
        list[1].Should().Be("beta");
    }

    [Fact]
    public void TrackCollection_Clear_ThenUndo_RestoresAllItems()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>();
        list.Add("x");
        list.Add("y");
        list.Add("z");

        using var observer = new UndoableCollectionObserver<string>(ctx, list, "Items");

        list.Clear();

        ctx.Undo();
        list.Count.Should().Be(3);
    }

    [Fact]
    public void TrackCollection_Replace_ThenUndo_RestoresOldItem()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>();
        list.Add("original");

        using var observer = new UndoableCollectionObserver<string>(ctx, list, "Items");

        list[0] = "replaced";

        ctx.Undo();
        list[0].Should().Be("original");
    }

    [Fact]
    public void TrackCollection_MultipleOps_ThenUndoAll()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>();

        using var observer = new UndoableCollectionObserver<string>(ctx, list, "Items");

        list.Add("a");
        list.Add("b");
        list.Add("c");

        ctx.Undo(); list.Count.Should().Be(2); // "a", "b"
        ctx.Undo(); list.Count.Should().Be(1); // "a"
        ctx.Undo(); list.Count.Should().Be(0); // empty
    }

    [Fact]
    public void Dispose_Unsubscribes_NoMoreTracking()
    {
        var ctx  = new UndoContext();
        var list = new StateList<string>();
        var obs  = new UndoableCollectionObserver<string>(ctx, list, "Items");
        obs.Dispose();

        list.Add("ghost");
        ctx.ShouldNotBeAbleToUndo();
    }

    [Fact]
    public void ManualRecordCollectionChange_Works()
    {
        var ctx   = new UndoContext();
        var list  = new StateList<string>();
        list.Add("before");

        var before = new[] { "before" };
        var after  = System.Array.Empty<string>();

        list.Clear();
        ctx.RecordCollectionChange(list,
            YFex.State.Notification.ChangeKind.ItemsCleared,
            before, after, "Items");

        ctx.Undo();
        list.Count.Should().Be(1);
        list[0].Should().Be("before");
    }
}
