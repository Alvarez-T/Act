using YFex.State.History;

namespace YFex.State.History.Tests;

/// <summary>
/// Unit tests for <see cref="UndoContext"/> stack operations, coalescing, savepoint,
/// navigation boundary, and eviction. No StateObject or generator required.
/// Uses the manual RecordPropertyChange API so the context mechanics are tested directly.
/// </summary>
public sealed class UndoContextTests
{
    // Shared static setter used across tests (simulates generated code)
    private static string _value = "";
    private static readonly Action<object, object?> s_setter =
        static (owner, v) => _value = (string)(v ?? "");

    private static void Record(UndoContext ctx, string propName, string old, string @new,
        int mergeMs = 0)
    {
        ctx.RecordPropertyChange(
            new object(), propName, s_setter, old, @new,
            DateTime.UtcNow.Ticks, mergeMs);
    }

    // ── Basic push/pop ────────────────────────────────────────────────────────

    [Fact]
    public void EmptyContext_CannotUndoOrRedo()
    {
        var ctx = new UndoContext();
        ctx.ShouldNotBeAbleToUndo();
        ctx.ShouldNotBeAbleToRedo();
        ctx.UndoDepth.Should().Be(0);
        ctx.RedoDepth.Should().Be(0);
    }

    [Fact]
    public void Record_IncreasesUndoDepth()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.ShouldHaveUndoDepth(1);
        ctx.ShouldBeAbleToUndo();
        ctx.ShouldNotBeAbleToRedo();
    }

    [Fact]
    public void Undo_DecreasesUndoDepth_IncreasesRedoDepth()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.Undo();
        ctx.ShouldHaveUndoDepth(0);
        ctx.ShouldHaveRedoDepth(1);
        ctx.ShouldNotBeAbleToUndo();
        ctx.ShouldBeAbleToRedo();
    }

    [Fact]
    public void Redo_AfterUndo_RestoresDepths()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.Undo();
        ctx.Redo();
        ctx.ShouldHaveUndoDepth(1);
        ctx.ShouldHaveRedoDepth(0);
    }

    [Fact]
    public void NewChange_AfterUndo_ClearsRedoStack()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.Undo();
        ctx.ShouldHaveRedoDepth(1);

        Record(ctx, "Name", "a", "c");
        ctx.ShouldHaveRedoDepth(0);
        ctx.ShouldNotBeAbleToRedo();
    }

    [Fact]
    public void Undo_InvokesSetterWithOldValue()
    {
        var ctx  = new UndoContext();
        _value   = "before";
        string capturedOld = _value;
        string capturedNew = "after";
        _value   = capturedNew;

        ctx.RecordPropertyChange(new object(), "Val", s_setter, capturedOld, capturedNew,
            DateTime.UtcNow.Ticks, 0);

        ctx.Undo();
        _value.Should().Be(capturedOld);
    }

    [Fact]
    public void Redo_InvokesSetterWithNewValue()
    {
        var ctx = new UndoContext();
        ctx.RecordPropertyChange(new object(), "Val", s_setter, "old", "new",
            DateTime.UtcNow.Ticks, 0);
        ctx.Undo();
        _value = "old"; // simulate the undo applied

        ctx.Redo();
        _value.Should().Be("new");
    }

    // ── Coalescing ────────────────────────────────────────────────────────────

    [Fact]
    public void SameProperty_WithinMergeWindow_Coalesces()
    {
        var ctx = new UndoContext();
        var owner = new object();
        long now  = DateTime.UtcNow.Ticks;

        ctx.RecordPropertyChange(owner, "Name", s_setter, "a", "b", now, 500);
        ctx.RecordPropertyChange(owner, "Name", s_setter, "b", "c", now + 100, 500); // within 500ms

        ctx.ShouldHaveUndoDepth(1); // coalesced into one group
    }

    [Fact]
    public void SameProperty_OutsideMergeWindow_DoesNotCoalesce()
    {
        var ctx   = new UndoContext();
        var owner = new object();
        long now  = DateTime.UtcNow.Ticks;
        long windowTicks = TimeSpan.FromMilliseconds(500).Ticks;

        ctx.RecordPropertyChange(owner, "Name", s_setter, "a", "b", now, 500);
        ctx.RecordPropertyChange(owner, "Name", s_setter, "b", "c", now + windowTicks + 1, 500);

        ctx.ShouldHaveUndoDepth(2);
    }

    [Fact]
    public void DifferentProperties_DoNotCoalesce()
    {
        var ctx   = new UndoContext();
        var owner = new object();
        long now  = DateTime.UtcNow.Ticks;

        ctx.RecordPropertyChange(owner, "First",  s_setter, "a", "b", now, 500);
        ctx.RecordPropertyChange(owner, "Second", s_setter, "x", "y", now, 500);

        ctx.ShouldHaveUndoDepth(2);
    }

    // ── Eviction ──────────────────────────────────────────────────────────────

    [Fact]
    public void MaxDepth_ExceededByOne_EvicessOldest()
    {
        var ctx = new UndoContext { MaxDepth = 3 };
        for (int i = 0; i < 4; i++)
            Record(ctx, $"P{i}", i.ToString(), (i + 1).ToString());

        ctx.ShouldHaveUndoDepth(3);
    }

    [Fact]
    public void MaxDepth_ManyOverflows_DepthStaysAtMax()
    {
        var ctx = new UndoContext { MaxDepth = 5 };
        for (int i = 0; i < 20; i++)
            Record(ctx, "Name", i.ToString(), (i + 1).ToString());

        ctx.ShouldHaveUndoDepth(5);
    }

    // ── Transactions ──────────────────────────────────────────────────────────

    [Fact]
    public void BeginTransaction_GroupsChangesIntoOneUndoStep()
    {
        var ctx = new UndoContext();
        using (ctx.BeginTransaction())
        {
            Record(ctx, "First",  "a", "b");
            Record(ctx, "Last",   "x", "y");
            Record(ctx, "Email",  "old@", "new@");
        }
        ctx.ShouldHaveUndoDepth(1);
    }

    [Fact]
    public void BeginTransaction_WithLabel_AppearsInHistory()
    {
        var ctx = new UndoContext();
        using (ctx.BeginTransaction("Renamed document"))
        {
            Record(ctx, "Title", "Old", "New");
        }
        ctx.UndoHistory.Should().HaveCount(1);
        ctx.UndoHistory[0].Label.Should().Be("Renamed document");
        ctx.UndoHistory[0].Description.Should().Be("Renamed document");
    }

    [Fact]
    public void EmptyTransaction_ProducesNoUndoEntry()
    {
        var ctx = new UndoContext();
        using (ctx.BeginTransaction()) { }
        ctx.ShouldHaveUndoDepth(0);
    }

    // ── SuspendRecording ──────────────────────────────────────────────────────

    [Fact]
    public void SuspendRecording_SuppressesDeltaCapture()
    {
        var ctx = new UndoContext();
        using (ctx.SuspendRecording())
        {
            Record(ctx, "Name", "a", "b");
        }
        ctx.ShouldHaveUndoDepth(0);
    }

    [Fact]
    public void SuspendRecording_AfterDispose_ResumesCapture()
    {
        var ctx = new UndoContext();
        using (ctx.SuspendRecording()) { }
        Record(ctx, "Name", "a", "b");
        ctx.ShouldHaveUndoDepth(1);
    }

    // ── Savepoint ─────────────────────────────────────────────────────────────

    [Fact]
    public void SetSavepoint_IsAtSavepoint_True_WhenDepthMatches()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.SetSavepoint();
        ctx.ShouldBeAtSavepoint();
    }

    [Fact]
    public void AfterSavepoint_NewChange_IsNotAtSavepoint()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.SetSavepoint();
        Record(ctx, "Name", "b", "c");
        ctx.ShouldNotBeAtSavepoint();
    }

    [Fact]
    public void UndoBackToSavepoint_IsAtSavepoint_True()
    {
        var ctx = new UndoContext();
        ctx.SetSavepoint();         // depth 0 = savepoint
        Record(ctx, "Name", "a", "b");
        ctx.Undo();
        ctx.ShouldBeAtSavepoint();
    }

    [Fact]
    public void ClearSavepoint_MakesIsAtSavepoint_False()
    {
        var ctx = new UndoContext();
        ctx.SetSavepoint();
        ctx.ClearSavepoint();
        ctx.IsAtSavepoint.Should().BeFalse();
    }

    // ── Navigation boundary ───────────────────────────────────────────────────

    [Fact]
    public void NavigationBoundary_BlocksUndoPastDepth()
    {
        var ctx = new UndoContext();
        Record(ctx, "A", "1", "2");
        ctx.PushNavigationBoundary();   // boundary at depth=1
        Record(ctx, "B", "3", "4");     // depth goes to 2

        ctx.Undo();                     // depth back to 1 (at boundary)
        ctx.ShouldNotBeAbleToUndo();    // blocked by boundary
    }

    [Fact]
    public void AllowUndoPastBoundary_True_AllowsUndoBeyond()
    {
        var ctx = new UndoContext { AllowUndoPastBoundary = true };
        Record(ctx, "A", "1", "2");
        ctx.PushNavigationBoundary();
        Record(ctx, "B", "3", "4");

        ctx.Undo(); // depth 1
        ctx.ShouldBeAbleToUndo(); // allowed past boundary
    }

    [Fact]
    public void PopNavigationBoundary_RemovesBoundary()
    {
        var ctx = new UndoContext();
        Record(ctx, "A", "1", "2");
        ctx.PushNavigationBoundary();
        ctx.PopNavigationBoundary();

        ctx.ShouldBeAbleToUndo(); // no boundary any more
    }

    // ── History visualization ──────────────────────────────────────────────────

    [Fact]
    public void UndoHistory_ReflectsStack_MostRecentFirst()
    {
        var ctx = new UndoContext();
        using (ctx.BeginTransaction("Step 1")) Record(ctx, "A", "1", "2");
        using (ctx.BeginTransaction("Step 2")) Record(ctx, "B", "3", "4");

        var hist = ctx.UndoHistory;
        hist.Should().HaveCount(2);
        hist[0].Label.Should().Be("Step 2"); // most recent first
        hist[1].Label.Should().Be("Step 1");
    }

    [Fact]
    public void RedoHistory_PopulatedAfterUndo()
    {
        var ctx = new UndoContext();
        Record(ctx, "Name", "a", "b");
        ctx.Undo();
        ctx.RedoHistory.Should().HaveCount(1);
    }
}
