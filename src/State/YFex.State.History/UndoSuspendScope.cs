namespace YFex.State.History;

/// <summary>
/// Temporarily suppresses undo recording. Use for programmatic initialization, default-value
/// seeding, or other cases where property changes should not create undo entries.
/// <para>
/// This is a <see langword="ref struct"/> and cannot cross <see langword="await"/> boundaries.
/// </para>
/// </summary>
public ref struct UndoSuspendScope
{
    private readonly UndoContext? _ctx;
    private bool _disposed;

    internal UndoSuspendScope(UndoContext ctx)
    {
        _ctx      = ctx;
        _disposed = false;
        _ctx.BeginSuspend();
    }

    /// <summary>Resumes undo recording.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx?.EndSuspend();
    }
}
