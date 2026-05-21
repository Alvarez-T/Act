namespace YFex.State.History;

/// <summary>
/// Delimits a manual undo transaction. All property/collection changes within the scope
/// are grouped into a single undo step. Dispose (or end of a <see langword="using"/> block)
/// commits the group.
/// <para>
/// This is a <see langword="ref struct"/> and cannot cross <see langword="await"/> boundaries.
/// For transactions that span async operations, use <see cref="UndoContext.BeginGroup"/> and
/// <see cref="UndoContext.EndGroup"/> directly with <see langword="try"/>/<see langword="finally"/>.
/// </para>
/// </summary>
public ref struct UndoTransactionScope
{
    private readonly UndoContext? _ctx;
    private bool _disposed;

    internal UndoTransactionScope(UndoContext ctx)
    {
        _ctx      = ctx;
        _disposed = false;
    }

    /// <summary>Commits the transaction (ends the open group).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx?.EndGroup();
    }
}
