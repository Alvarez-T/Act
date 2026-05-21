using YFex.State.History.Persistence;

namespace YFex.Persistence.Tests.Providers;

public sealed class UndoSnapshotProviderTests
{
    // ── Capture with empty context ─────────────────────────────────────────────

    [Fact]
    public async Task CaptureAsync_ReturnsNull_WhenContextIsEmpty()
    {
        var context = new UndoContext();
        var provider = BuildProvider(context);

        var data = await provider.CaptureAsync();

        data.Should().BeNull("empty context → nothing to persist");
    }

    // ── Round-trip: record → capture → restore ─────────────────────────────────

    [Fact]
    public async Task RoundTrip_RestoresUndoStack_WithCorrectCount()
    {
        var context = new UndoContext();
        var vm = new SimpleUndoable();
        RecordChange(context, vm, "Name", null, "Alice");
        RecordChange(context, vm, "Name", "Alice", "Bob");

        var provider = BuildProvider(context);
        var data = await provider.CaptureAsync();
        data.Should().NotBeNull();

        var fresh = new UndoContext();
        var freshVm = new SimpleUndoable();
        var restore = BuildProvider(fresh,
            ownerResolver: _ => freshVm,
            setterResolver: (_, prop) => prop == "Name"
                ? (obj, v) => ((SimpleUndoable)obj).Name = (string?)v ?? ""
                : null);

        await restore.RestoreAsync(data!, storedVersion: 1);

        fresh.UndoDepth.Should().Be(2);
    }

    [Fact]
    public async Task RoundTrip_UndoRestored_Works()
    {
        var context = new UndoContext();
        var vm = new SimpleUndoable { Name = "Alice" };
        RecordChange(context, vm, "Name", "", "Alice",
            setter: (obj, v) => ((SimpleUndoable)obj).Name = (string?)v ?? "");

        var provider = BuildProvider(context);
        var data = await provider.CaptureAsync();

        var freshVm = new SimpleUndoable { Name = "Alice" };
        var freshCtx = new UndoContext();
        var restore = BuildProvider(freshCtx,
            ownerResolver: _ => freshVm,
            setterResolver: (_, _) => (obj, v) => ((SimpleUndoable)obj).Name = (string?)v ?? "");
        await restore.RestoreAsync(data!, storedVersion: 1);

        freshCtx.Undo();

        freshVm.Name.Should().Be("", "undo reverts Alice → empty");
    }

    // ── Version mismatch ───────────────────────────────────────────────────────

    [Fact]
    public async Task RestoreAsync_Skips_OnVersionMismatch()
    {
        var context = new UndoContext();
        var vm = new SimpleUndoable();
        RecordChange(context, vm, "Name", null, "X");

        var provider = BuildProvider(context, version: 1);
        var data = await provider.CaptureAsync();

        var freshCtx = new UndoContext();
        var freshProvider = BuildProvider(freshCtx, version: 2); // mismatch
        await freshProvider.RestoreAsync(data!, storedVersion: 1);

        freshCtx.UndoDepth.Should().Be(0, "version mismatch skips restore");
    }

    // ── Missing owner / setter ─────────────────────────────────────────────────

    [Fact]
    public async Task RestoreAsync_SkipsDelta_WhenOwnerCannotBeResolved()
    {
        var context = new UndoContext();
        var vm = new SimpleUndoable();
        RecordChange(context, vm, "Name", null, "Y");

        var provider = BuildProvider(context);
        var data = await provider.CaptureAsync();

        var freshCtx = new UndoContext();
        var restore = BuildProvider(freshCtx, ownerResolver: _ => null); // can't resolve
        await restore.RestoreAsync(data!, storedVersion: 1);

        freshCtx.UndoDepth.Should().Be(0, "unresolvable owner → delta skipped → no groups");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static UndoSnapshotProvider BuildProvider(
        UndoContext context,
        int version = 1,
        Func<string, object?>? ownerResolver = null,
        Func<string, string, Action<object, object?>?>? setterResolver = null)
    {
        var p = new UndoSnapshotProvider(context, "test-undo", version);
        if (ownerResolver is not null)  p.OwnerResolver  = ownerResolver;
        if (setterResolver is not null) p.SetterResolver = setterResolver;
        return p;
    }

    private static void RecordChange(
        UndoContext ctx, object owner, string property,
        object? oldValue, object? newValue,
        Action<object, object?>? setter = null)
    {
        setter ??= (_, _) => { };
        ctx.RecordPropertyChange(owner, property, setter, oldValue, newValue,
            DateTime.UtcNow.Ticks, mergeWindowMs: 0);
    }

    private sealed class SimpleUndoable { public string Name { get; set; } = ""; }
}
