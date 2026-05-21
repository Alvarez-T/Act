using System;
using System.Collections.Generic;
using System.ComponentModel;
using YFex.State.Commands;
using YFex.State.History.Internal;

namespace YFex.State.History;

/// <summary>
/// Manages an undo/redo stack for one named scope. Tracks property and collection changes
/// as typed delta records and exposes them through <see cref="UndoCommand"/> /
/// <see cref="RedoCommand"/> <see cref="IStateCommand"/> instances that can be bound to
/// any UI platform.
/// </summary>
public sealed class UndoContext
{
    // ── Stack state ───────────────────────────────────────────────────────────
    private UndoGroup? _undoHead;   // most recent (top)
    private UndoGroup? _undoTail;   // oldest (bottom) — for O(1) eviction
    private UndoGroup? _redoHead;
    private int        _undoDepth;
    private int        _redoDepth;
    private int        _maxDepth = 100;

    // ── In-flight group (manual transaction or batch) ─────────────────────────
    private UndoGroup? _openGroup;
    private int        _openGroupNesting;  // for nested BeginGroup/EndGroup calls

    // ── Reentrancy and suspension ─────────────────────────────────────────────
    private bool _isReplaying;
    private int  _suspendCount;
    private List<Func<bool>>? _suspensionPredicates;

    // ── Navigation boundary ───────────────────────────────────────────────────
    private int _navigationBoundaryDepth = -1;

    // ── Savepoint ─────────────────────────────────────────────────────────────
    private int _savepointDepth = -1;
    private bool _wasAtSavepointLastCheck;

    // ── Object pool ───────────────────────────────────────────────────────────
    private UndoGroup? _poolHead;
    private int        _poolSize;
    private const int  MaxPoolSize = 16;

    // ── Commands (lazy-init) ──────────────────────────────────────────────────
    private UndoCommandImpl? _undoCmd;
    private RedoCommandImpl? _redoCmd;

    // ── Internal event for UndoManager coordination ───────────────────────────
    internal event Action? StackChanged;

    // ── Public configuration ──────────────────────────────────────────────────

    /// <summary>Maximum number of undo groups retained. Default 100.</summary>
    public int MaxDepth
    {
        get => _maxDepth;
        set => _maxDepth = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    /// Default coalescing window in milliseconds. Consecutive same-property changes within
    /// this window are merged into a single undo entry. Default 500 ms. 0 disables coalescing.
    /// </summary>
    public int MergeWindowMs { get; set; } = 500;

    /// <summary>Current depth of the undo stack.</summary>
    public int UndoDepth => _undoDepth;

    /// <summary>Current depth of the redo stack.</summary>
    public int RedoDepth => _redoDepth;

    /// <summary>
    /// <see langword="true"/> when there are entries to undo and the context is not suspended.
    /// </summary>
    public bool CanUndo =>
        _undoDepth > 0 &&
        !IsSuspendedInternal &&
        (_navigationBoundaryDepth < 0 ||
         AllowUndoPastBoundary ||
         _undoDepth > _navigationBoundaryDepth);

    /// <summary>
    /// <see langword="true"/> when there are entries to redo and the context is not suspended.
    /// </summary>
    public bool CanRedo => _redoDepth > 0 && !IsSuspendedInternal;

    /// <summary>
    /// When <see langword="true"/>, <see cref="Undo"/> can step past a navigation boundary.
    /// Default <see langword="false"/>.
    /// </summary>
    public bool AllowUndoPastBoundary { get; set; }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Command that performs one undo step.</summary>
    public IStateCommand UndoCommand => _undoCmd ??= new UndoCommandImpl(this);

    /// <summary>Command that performs one redo step.</summary>
    public IStateCommand RedoCommand => _redoCmd ??= new RedoCommandImpl(this);

    // ── Infrastructure (called by generated code — not intended for direct use) ─

    /// <summary>Infrastructure. <see langword="true"/> while undo/redo is being applied.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsReplaying => _isReplaying;

    /// <summary>Infrastructure. <see langword="true"/> when recording is suppressed.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public bool IsSuspended => _suspendCount > 0 || CheckSuspensionPredicates();

    private bool IsSuspendedInternal => _suspendCount > 0 || CheckSuspensionPredicates();

    /// <summary>
    /// Infrastructure. Records a property change delta. Called by generated
    /// <c>OnXxxChanging</c> partial method implementations.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void RecordPropertyChange(
        object owner,
        string propertyName,
        Action<object, object?> setter,
        object? oldValue,
        object? newValue,
        long timestampTicks,
        int mergeWindowMs)
    {
        if (_isReplaying || IsSuspendedInternal) return;

        var delta = new UndoDelta(owner, propertyName, setter, oldValue, newValue, timestampTicks, mergeWindowMs);

        if (_openGroup is not null)
        {
            _openGroup.AppendDelta(delta);
            return;
        }

        // Try to coalesce with the top group (single-delta group, within merge window)
        if (_undoHead is not null &&
            _undoHead.Count == 1 &&
            _undoHead.First!.CanMergeWith(delta))
        {
            _undoHead.First.MergeWith(delta);
            _undoHead.Timestamp = DateTime.UtcNow;
            NotifyCommandsChanged();
            return;
        }

        var group = RentGroup();
        group.AppendDelta(delta);
        group.Timestamp = DateTime.UtcNow;
        PushUndo(group, clearRedo: true);
        NotifyCommandsChanged();
    }

    /// <summary>Infrastructure. Records a collection change delta.</summary>
    internal void RecordCollectionDelta(UndoDeltaBase delta)
    {
        if (_isReplaying || IsSuspendedInternal) return;

        if (_openGroup is not null)
        {
            _openGroup.AppendDelta(delta);
            return;
        }

        var group = RentGroup();
        group.AppendDelta(delta);
        group.Timestamp = DateTime.UtcNow;
        PushUndo(group, clearRedo: true);
        NotifyCommandsChanged();
    }

    /// <summary>Infrastructure. Opens a group scope (batch or transaction start).</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginGroup()
    {
        if (_isReplaying || IsSuspendedInternal) return;
        if (_openGroupNesting++ == 0)
            _openGroup ??= RentGroup();
    }

    /// <summary>Infrastructure. Closes the current group scope.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void EndGroup()
    {
        if (_openGroupNesting <= 0) return;
        if (--_openGroupNesting > 0) return;

        var group = _openGroup;
        _openGroup = null;

        if (group is null || group.Count == 0)
        {
            if (group is not null) ReturnGroup(group);
            return;
        }

        group.Timestamp = DateTime.UtcNow;
        PushUndo(group, clearRedo: true);
        NotifyCommandsChanged();
    }

    /// <summary>Infrastructure. Increments the suspension counter.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void BeginSuspend() => _suspendCount++;

    /// <summary>Infrastructure. Decrements the suspension counter.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void EndSuspend()
    {
        if (_suspendCount > 0) _suspendCount--;
    }

    /// <summary>Infrastructure. Fires CanExecuteChanged on both commands.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void NotifyCommandsChanged()
    {
        _undoCmd?.Notify();
        _redoCmd?.Notify();
        NotifySavepointChanged();
        StackChanged?.Invoke();
    }

    /// <summary>
    /// Infrastructure. The timestamp of the most recent undo group. Used by
    /// <see cref="UndoManager"/> to determine the globally most-recent change.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public DateTime? UndoTopTimestamp => _undoHead?.Timestamp;

    // ── Operations ────────────────────────────────────────────────────────────

    /// <summary>Reverts the most recent change group.</summary>
    public void Undo()
    {
        if (!CanUndo) return;
        var group = PopUndo();
        _isReplaying = true;
        try   { ApplyGroupReverse(group); }
        finally { _isReplaying = false; }
        PushRedo(group);
        NotifyCommandsChanged();
    }

    /// <summary>Re-applies the most recently undone change group.</summary>
    public void Redo()
    {
        if (!CanRedo) return;
        var group = PopRedo();
        _isReplaying = true;
        try   { ApplyGroupForward(group); }
        finally { _isReplaying = false; }
        PushUndo(group, clearRedo: false);
        NotifyCommandsChanged();
    }

    // ── Scoped helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Opens a manual transaction: all changes until the returned scope is disposed are
    /// grouped into a single undo step.
    /// </summary>
    public UndoTransactionScope BeginTransaction(string? label = null)
    {
        BeginGroup();
        if (label is not null && _openGroup is not null)
            _openGroup.Label = label;
        return new UndoTransactionScope(this);
    }

    /// <summary>Suppresses recording for the duration of the returned scope.</summary>
    public UndoSuspendScope SuspendRecording() => new(this);

    // ── Savepoint ─────────────────────────────────────────────────────────────

    /// <summary>Marks the current undo depth as "clean" (e.g., after a save operation).</summary>
    public void SetSavepoint()
    {
        _savepointDepth = _undoDepth;
        _wasAtSavepointLastCheck = true;
        NotifyCommandsChanged();
    }

    /// <summary>Clears the savepoint marker.</summary>
    public void ClearSavepoint()
    {
        _savepointDepth = -1;
        _wasAtSavepointLastCheck = false;
        NotifyCommandsChanged();
    }

    /// <summary>
    /// <see langword="true"/> when the current undo depth equals the savepoint depth,
    /// meaning the state matches the last saved state.
    /// </summary>
    public bool IsAtSavepoint => _savepointDepth >= 0 && _undoDepth == _savepointDepth;

    /// <summary>
    /// Fires when <see cref="IsAtSavepoint"/> transitions between <see langword="true"/> and
    /// <see langword="false"/>. Useful for updating a "dirty" indicator in the UI.
    /// </summary>
    public event Action? SavepointChanged;

    // ── Navigation boundary ───────────────────────────────────────────────────

    /// <summary>
    /// Marks the current undo depth as a navigation boundary. <see cref="Undo"/> will not
    /// step past this point unless <see cref="AllowUndoPastBoundary"/> is set.
    /// Typically called from the generated <c>OnActivated</c> override.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void PushNavigationBoundary() => _navigationBoundaryDepth = _undoDepth;

    /// <summary>Removes the navigation boundary. Typically called from the generated
    /// <c>OnDeactivated</c> override.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void PopNavigationBoundary() => _navigationBoundaryDepth = -1;

    // ── Async-command suspension ──────────────────────────────────────────────

    /// <summary>
    /// Registers an async command so that <see cref="CanUndo"/> and <see cref="CanRedo"/>
    /// return <see langword="false"/> while the command is executing.
    /// Typically wired automatically by the generator when both <c>[Undoable]</c> and
    /// <c>[StateCommand]</c> are present on the same class.
    /// </summary>
    public void RegisterCommandSuspension(IAsyncStateCommand command)
    {
        if (command is null) return;
        _suspensionPredicates ??= new List<Func<bool>>();
        _suspensionPredicates.Add(() => command.IsExecuting);
        command.CanExecuteChanged += NotifyCommandsChanged;
    }

    // ── History visualization ─────────────────────────────────────────────────

    /// <summary>Read-only view of the undo stack, most recent first.</summary>
    public IReadOnlyList<UndoHistoryEntry> UndoHistory => BuildHistoryList(_undoHead, forward: false);

    /// <summary>Read-only view of the redo stack, most recent first.</summary>
    public IReadOnlyList<UndoHistoryEntry> RedoHistory => BuildHistoryList(_redoHead, forward: true);

    // ── Persistence capture / restore ────────────────────────────────────────

    /// <summary>
    /// Serializes the undo stack into a flat list of <see cref="UndoCaptureGroup"/>,
    /// ordered from oldest to newest. Only <see cref="UndoDelta"/> entries (single-property
    /// changes) are captured; collection deltas are skipped.
    /// </summary>
    public IReadOnlyList<UndoCaptureGroup> CaptureUndoStack()
        => CaptureStack(_undoHead, forward: false);

    /// <summary>Serializes the redo stack into a flat list, ordered oldest to newest.</summary>
    public IReadOnlyList<UndoCaptureGroup> CaptureRedoStack()
        => CaptureStack(_redoHead, forward: true);

    /// <summary>
    /// Rebuilds the undo and redo stacks from previously captured groups.
    /// The setter in each <see cref="UndoCaptureDelta"/> is looked up via a
    /// caller-supplied resolver; deltas whose owner or property cannot be resolved are skipped.
    /// </summary>
    public void RestoreStacks(
        IReadOnlyList<UndoCaptureGroup> undoGroups,
        IReadOnlyList<UndoCaptureGroup> redoGroups,
        Func<string, string, Action<object, object?>?> setterResolver,
        Func<string, object?> ownerResolver)
    {
        Clear();

        foreach (var capture in undoGroups)
        {
            var group = BuildGroupFromCapture(capture, setterResolver, ownerResolver);
            if (group is null) continue;
            PushUndo(group, clearRedo: false);
        }

        foreach (var capture in redoGroups)
        {
            var group = BuildGroupFromCapture(capture, setterResolver, ownerResolver);
            if (group is null) continue;
            PushRedo(group);
        }

        NotifyCommandsChanged();
    }

    /// <summary>Clears both undo and redo stacks without recording the clear.</summary>
    public void Clear()
    {
        while (_undoHead is not null) PopUndo().Reset();
        _undoDepth = 0;
        ClearRedoStack();
        NotifyCommandsChanged();
    }

    private static IReadOnlyList<UndoCaptureGroup> CaptureStack(UndoGroup? head, bool forward)
    {
        var result = new List<UndoCaptureGroup>();
        var node   = head;
        while (node is not null)
        {
            var deltas = new List<UndoCaptureDelta>();
            var d = node.First;
            while (d is not null)
            {
                if (d is UndoDelta ud)
                {
                    deltas.Add(new UndoCaptureDelta(
                        ud.Owner.GetType().FullName ?? ud.Owner.GetType().Name,
                        ud._propertyName,
                        ud.OldValue,
                        ud.NewValue,
                        ud.TimestampTicks,
                        ud.MergeWindowMs));
                }
                d = d.Next;
            }

            if (deltas.Count > 0)
                result.Add(new UndoCaptureGroup(node.Label, node.Timestamp, deltas));

            node = forward ? node.PreviousRedo : node.PreviousUndo;
        }

        result.Reverse(); // oldest first
        return result;
    }

    private UndoGroup? BuildGroupFromCapture(
        UndoCaptureGroup capture,
        Func<string, string, Action<object, object?>?> setterResolver,
        Func<string, object?> ownerResolver)
    {
        UndoGroup? group = null;
        foreach (var d in capture.Deltas)
        {
            var owner  = ownerResolver(d.OwnerTypeName);
            var setter = owner is not null ? setterResolver(d.OwnerTypeName, d.PropertyName) : null;
            if (owner is null || setter is null) continue;

            var delta = new UndoDelta(owner, d.PropertyName, setter,
                d.OldValue, d.NewValue, d.TimestampTicks, d.MergeWindowMs);
            group ??= RentGroup();
            group.AppendDelta(delta);
        }

        if (group is not null)
        {
            group.Label     = capture.Label;
            group.Timestamp = capture.Timestamp;
        }

        return group;
    }

    // ── Manual collection recording escape hatch ──────────────────────────────

    /// <summary>
    /// Records a collection mutation for undo purposes. Call this from an
    /// <see cref="IChangedHandler"/> on a <see cref="YFex.State.Collections.StateList{T}"/>
    /// when automatic <c>[UndoableCollection]</c> generation is not used.
    /// </summary>
    public void RecordCollectionChange<T>(
        YFex.State.Collections.StateList<T> list,
        YFex.State.Notification.ChangeKind kind,
        T[] beforeSnapshot,
        T[] afterSnapshot,
        string propertyName = "")
    {
        var delta = new CollectionUndoDelta<T>(list, beforeSnapshot, afterSnapshot, propertyName);
        RecordCollectionDelta(delta);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void PushUndo(UndoGroup group, bool clearRedo)
    {
        if (clearRedo) ClearRedoStack();

        // Link as new head
        group.NextUndo    = null;
        group.PreviousUndo = _undoHead;
        if (_undoHead is not null) _undoHead.NextUndo = group;
        _undoHead = group;
        if (_undoTail is null) _undoTail = group;
        _undoDepth++;

        // Evict oldest if over limit
        while (_undoDepth > _maxDepth) EvictOldestUndo();
    }

    private UndoGroup PopUndo()
    {
        var group = _undoHead!;
        _undoHead = group.PreviousUndo;
        if (_undoHead is not null) _undoHead.NextUndo = null;
        else _undoTail = null;
        group.PreviousUndo = null;
        _undoDepth--;
        return group;
    }

    private void PushRedo(UndoGroup group)
    {
        group.PreviousRedo = _redoHead;
        _redoHead = group;
        _redoDepth++;
    }

    private UndoGroup PopRedo()
    {
        var group = _redoHead!;
        _redoHead = group.PreviousRedo;
        group.PreviousRedo = null;
        _redoDepth--;
        return group;
    }

    private void ClearRedoStack()
    {
        var node = _redoHead;
        while (node is not null)
        {
            var next = node.PreviousRedo;
            node.Reset();
            ReturnGroup(node);
            node = next;
        }
        _redoHead  = null;
        _redoDepth = 0;
    }

    private void EvictOldestUndo()
    {
        if (_undoTail is null) return;
        var oldest = _undoTail;
        _undoTail = oldest.NextUndo;
        if (_undoTail is not null) _undoTail.PreviousUndo = null;
        else _undoHead = null;
        oldest.NextUndo = null;
        _undoDepth--;
        oldest.Reset();
        ReturnGroup(oldest);
    }

    private static void ApplyGroupReverse(UndoGroup group)
    {
        // Build a reversed traversal list (delta chain is forward-only)
        var stack = new System.Collections.Generic.Stack<UndoDeltaBase>(group.Count);
        var node  = group.First;
        while (node is not null) { stack.Push(node); node = node.Next; }
        while (stack.Count > 0) stack.Pop().ApplyUndo();
    }

    private static void ApplyGroupForward(UndoGroup group)
    {
        var node = group.First;
        while (node is not null) { node.ApplyRedo(); node = node.Next; }
    }

    private bool CheckSuspensionPredicates()
    {
        if (_suspensionPredicates is null) return false;
        for (int i = 0; i < _suspensionPredicates.Count; i++)
            if (_suspensionPredicates[i]()) return true;
        return false;
    }

    private void NotifySavepointChanged()
    {
        bool now = IsAtSavepoint;
        if (now != _wasAtSavepointLastCheck)
        {
            _wasAtSavepointLastCheck = now;
            SavepointChanged?.Invoke();
        }
    }

    private UndoGroup RentGroup()
    {
        if (_poolHead is not null)
        {
            var g = _poolHead;
            _poolHead = g.PoolNext;
            g.PoolNext = null;
            _poolSize--;
            return g;
        }
        return new UndoGroup();
    }

    private void ReturnGroup(UndoGroup group)
    {
        if (_poolSize >= MaxPoolSize) return;
        group.Reset();
        group.PoolNext = _poolHead;
        _poolHead      = group;
        _poolSize++;
    }

    private static List<UndoHistoryEntry> BuildHistoryList(UndoGroup? head, bool forward)
    {
        var list = new List<UndoHistoryEntry>();
        var node = head;
        while (node is not null)
        {
            list.Add(new UndoHistoryEntry(
                node.Label,
                node.BuildDescription(),
                node.Timestamp,
                node.Count));
            node = forward ? node.PreviousRedo : node.PreviousUndo;
        }
        return list;
    }

    // ── Inner command implementations ─────────────────────────────────────────

    private sealed class UndoCommandImpl : IStateCommand
    {
        private readonly UndoContext _ctx;
        internal UndoCommandImpl(UndoContext ctx) => _ctx = ctx;
        public event Action? CanExecuteChanged;
        public bool CanExecute() => _ctx.CanUndo;
        public void Execute()    => _ctx.Undo();
        internal void Notify()   => CanExecuteChanged?.Invoke();
    }

    private sealed class RedoCommandImpl : IStateCommand
    {
        private readonly UndoContext _ctx;
        internal RedoCommandImpl(UndoContext ctx) => _ctx = ctx;
        public event Action? CanExecuteChanged;
        public bool CanExecute() => _ctx.CanRedo;
        public void Execute()    => _ctx.Redo();
        internal void Notify()   => CanExecuteChanged?.Invoke();
    }
}
