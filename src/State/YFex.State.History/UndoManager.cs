using System;
using System.Collections.Generic;
using System.ComponentModel;
using YFex.State.Commands;

namespace YFex.State.History;

/// <summary>
/// Coordinates multiple <see cref="UndoContext"/> instances into one combined undo/redo stream.
/// Useful when a document is composed of several ViewModels each with their own context, but the
/// user should see a single Undo menu item.
/// <para>
/// The manager routes each undo/redo operation to the context whose most-recent change has the
/// latest timestamp across all registered contexts.
/// </para>
/// </summary>
public sealed class UndoManager
{
    private readonly List<UndoContext> _contexts = new();
    private UndoManagerCommandImpl? _undoCmd;
    private UndoManagerCommandImpl? _redoCmd;

    // ── Registration ──────────────────────────────────────────────────────────

    /// <summary>Adds a context to the coordination pool.</summary>
    public void Register(UndoContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (_contexts.Contains(context)) return;
        _contexts.Add(context);
        context.StackChanged += OnContextChanged;
        NotifyCommandsChanged();
    }

    /// <summary>Removes a context from the coordination pool.</summary>
    public void Unregister(UndoContext context)
    {
        if (context is null) return;
        if (_contexts.Remove(context))
        {
            context.StackChanged -= OnContextChanged;
            NotifyCommandsChanged();
        }
    }

    // ── State ──────────────────────────────────────────────────────────────────

    /// <summary>True when any registered context has undo entries available.</summary>
    public bool CanUndo => FindMostRecentUndoContext() is not null;

    /// <summary>True when any registered context has redo entries available.</summary>
    public bool CanRedo => FindMostRecentRedoContext() is not null;

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Command that performs one undo step on the globally most-recent context.</summary>
    public IStateCommand UndoCommand => _undoCmd ??= new UndoManagerCommandImpl(this, undo: true);

    /// <summary>Command that performs one redo step on the globally most-recent context.</summary>
    public IStateCommand RedoCommand => _redoCmd ??= new UndoManagerCommandImpl(this, undo: false);

    // ── Operations ────────────────────────────────────────────────────────────

    /// <summary>
    /// Undoes the most recently recorded change across all registered contexts.
    /// </summary>
    public void Undo() => FindMostRecentUndoContext()?.Undo();

    /// <summary>
    /// Redoes the most recently undone change across all registered contexts.
    /// </summary>
    public void Redo() => FindMostRecentRedoContext()?.Redo();

    // ── Savepoint ─────────────────────────────────────────────────────────────

    /// <summary>Sets a savepoint on all registered contexts simultaneously.</summary>
    public void SetSavepoint()
    {
        foreach (var ctx in _contexts) ctx.SetSavepoint();
    }

    /// <summary>True when ALL registered contexts are at their respective savepoints.</summary>
    public bool IsAtSavepoint
    {
        get
        {
            if (_contexts.Count == 0) return true;
            foreach (var ctx in _contexts)
                if (!ctx.IsAtSavepoint) return false;
            return true;
        }
    }

    // ── History visualization ─────────────────────────────────────────────────

    /// <summary>
    /// Merged undo history across all contexts, sorted most-recent first.
    /// </summary>
    public IReadOnlyList<UndoHistoryEntry> UndoHistory
    {
        get
        {
            var merged = new List<UndoHistoryEntry>();
            foreach (var ctx in _contexts)
                merged.AddRange(ctx.UndoHistory);
            merged.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return merged;
        }
    }

    /// <summary>
    /// Merged redo history across all contexts, sorted most-recent first.
    /// </summary>
    public IReadOnlyList<UndoHistoryEntry> RedoHistory
    {
        get
        {
            var merged = new List<UndoHistoryEntry>();
            foreach (var ctx in _contexts)
                merged.AddRange(ctx.RedoHistory);
            merged.Sort(static (a, b) => b.Timestamp.CompareTo(a.Timestamp));
            return merged;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private UndoContext? FindMostRecentUndoContext()
    {
        UndoContext? best      = null;
        DateTime?   bestTime  = null;

        foreach (var ctx in _contexts)
        {
            if (!ctx.CanUndo) continue;
            var t = ctx.UndoTopTimestamp;
            if (t is null) continue;
            if (bestTime is null || t.Value > bestTime.Value)
            {
                best     = ctx;
                bestTime = t;
            }
        }
        return best;
    }

    private UndoContext? FindMostRecentRedoContext()
    {
        UndoContext? best     = null;
        DateTime?   bestTime = null;

        foreach (var ctx in _contexts)
        {
            if (!ctx.CanRedo) continue;
            // Use undo history length as a proxy — whichever context most recently performed an undo
            // (has the most-recently popped group) is the right target.
            // Since redo groups don't expose their timestamp directly, approximate with RedoDepth > 0.
            // TODO V2: expose RedoTopTimestamp on UndoContext for precise ordering.
            best = ctx;
        }
        _ = bestTime; // suppress unused warning
        return best;
    }

    private void OnContextChanged() => NotifyCommandsChanged();

    private void NotifyCommandsChanged()
    {
        _undoCmd?.Notify();
        _redoCmd?.Notify();
    }

    private sealed class UndoManagerCommandImpl : IStateCommand
    {
        private readonly UndoManager _mgr;
        private readonly bool        _undo;
        internal UndoManagerCommandImpl(UndoManager mgr, bool undo) { _mgr = mgr; _undo = undo; }
        public event Action? CanExecuteChanged;
        public bool CanExecute() => _undo ? _mgr.CanUndo : _mgr.CanRedo;
        public void Execute()    { if (_undo) _mgr.Undo(); else _mgr.Redo(); }
        internal void Notify()   => CanExecuteChanged?.Invoke();
    }
}
