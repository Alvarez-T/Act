using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using YFex.State.Collections.Internal;
using YFex.State.Notification;

namespace YFex.State.Collections;

/// <summary>
/// Zero-allocation observable list backed by a pooled <typeparamref name="T"/>[] array.
/// Implements <see cref="INotifyChanged"/> (not <c>INotifyCollectionChanged</c>) for pure-C#
/// consumers. XAML binding engines require a MVVM adapter
/// (<c>StateList.ToMvvmCollectionView()</c> in YFex.State.Mvvm).
/// </summary>
public sealed class StateList<T> : INotifyChanged, IActivatable, IDisposable,
    IReadOnlyList<T>
{
    private static readonly ChangedNotification s_addedDesc =
        new() { PropertyName = "Items", PropertyId = 0u, Kind = ChangeKind.ItemsAdded };
    private static readonly ChangedNotification s_removedDesc =
        new() { PropertyName = "Items", PropertyId = 0u, Kind = ChangeKind.ItemsRemoved };
    private static readonly ChangedNotification s_replacedDesc =
        new() { PropertyName = "Items", PropertyId = 0u, Kind = ChangeKind.ItemReplaced };
    private static readonly ChangedNotification s_clearedDesc =
        new() { PropertyName = "Items", PropertyId = 0u, Kind = ChangeKind.ItemsCleared };

    private T[] _items;
    private int _count;
    private List<IChangedHandler> _handlers = new(2);
    private bool _isActive;
    private bool _disposed;

    // Weak item-listener list (only populated when T : INotifyChanged)
    private List<WeakChangedHandler>? _itemListeners;

    public StateList(int initialCapacity = 8)
    {
        _items = ArrayPool<T>.Shared.Rent(Math.Max(initialCapacity, 4));
    }

    // ── IReadOnlyList<T> ─────────────────────────────────────────────────────

    public int Count => _count;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) ThrowOutOfRange();
            return _items[index];
        }
        set
        {
            if ((uint)index >= (uint)_count) ThrowOutOfRange();
            T old = _items[index];
            _items[index] = value;
            var desc = s_replacedDesc with { Index = index, Count = 1, OldItem = old };
            NotifyAll(in desc);
        }
    }

    // ── Mutation ─────────────────────────────────────────────────────────────

    public void Add(T item)
    {
        EnsureCapacity(_count + 1);
        _items[_count] = item;
        int idx = _count++;
        AttachListener(item);
        var desc = s_addedDesc with { Index = idx, Count = 1 };
        NotifyAll(in desc);
    }

    public void AddRange(ReadOnlySpan<T> items)
    {
        if (items.IsEmpty) return;
        EnsureCapacity(_count + items.Length);
        int startIdx = _count;
        items.CopyTo(_items.AsSpan(_count));
        foreach (var item in items) AttachListener(item);
        _count += items.Length;
        var desc = s_addedDesc with { Index = startIdx, Count = items.Length };
        NotifyAll(in desc);
    }

    public bool Remove(T item)
    {
        int idx = IndexOf(item);
        if (idx < 0) return false;
        RemoveAt(idx);
        return true;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_count) ThrowOutOfRange();
        DetachListener(_items[index]);
        _items.AsSpan(index + 1, _count - index - 1).CopyTo(_items.AsSpan(index));
        _items[--_count] = default!;
        var desc = s_removedDesc with { Index = index, Count = 1 };
        NotifyAll(in desc);
    }

    public void Clear()
    {
        DetachAllListeners();
        _items.AsSpan(0, _count).Clear();
        _count = 0;
        NotifyAll(in s_clearedDesc);
    }

    public int IndexOf(T item)
    {
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _count; i++)
            if (comparer.Equals(_items[i], item)) return i;
        return -1;
    }

    /// <summary>High-performance zero-allocation iteration.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<T> AsSpan() => _items.AsSpan(0, _count);

    // ── INotifyChanged ───────────────────────────────────────────────────────

    public void Subscribe(IChangedHandler handler) { if (!_handlers.Contains(handler)) _handlers.Add(handler); }
    public void Unsubscribe(IChangedHandler handler) => _handlers.Remove(handler);

    private void NotifyAll(in ChangedNotification desc)
    {
        for (int i = 0; i < _handlers.Count; i++)
            _handlers[i].OnChanged(this, in desc);
    }

    // ── IActivatable ─────────────────────────────────────────────────────────

    public bool IsActive => _isActive;

    public void Activate()
    {
        if (_isActive) return;
        _isActive = true;
        if (typeof(IActivatable).IsAssignableFrom(typeof(T)))
        {
            for (int i = 0; i < _count; i++)
                (_items[i] as IActivatable)?.Activate();
        }
    }

    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;
        if (typeof(IActivatable).IsAssignableFrom(typeof(T)))
        {
            for (int i = 0; i < _count; i++)
                (_items[i] as IActivatable)?.Deactivate();
        }
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_isActive) Deactivate();
        DetachAllListeners();
        ArrayPool<T>.Shared.Return(_items, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = Array.Empty<T>();
        _count = 0;
    }

    // ── IEnumerator ──────────────────────────────────────────────────────────

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── Weak item-change listeners ────────────────────────────────────────────

    private void AttachListener(T item)
    {
        if (item is not INotifyChanged nc) return;
        _itemListeners ??= new List<WeakChangedHandler>(4);
        var handler = new WeakChangedHandler(new ItemChangedRelay(this));
        _itemListeners.Add(handler);
        nc.Subscribe(handler);

        if (_isActive && item is IActivatable act)
            act.Activate();
    }

    private void DetachListener(T item)
    {
        if (item is IActivatable act && _isActive) act.Deactivate();
        // Listeners are cleaned up lazily on the next reap pass; no strong ref held
    }

    private void DetachAllListeners()
    {
        _itemListeners?.Clear();
        if (!_isActive) return;
        if (typeof(IActivatable).IsAssignableFrom(typeof(T)))
            for (int i = 0; i < _count; i++)
                (_items[i] as IActivatable)?.Deactivate();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void EnsureCapacity(int required)
    {
        if (required <= _items.Length) return;
        int newSize = Math.Max(_items.Length * 2, required);
        var grown = ArrayPool<T>.Shared.Rent(newSize);
        _items.AsSpan(0, _count).CopyTo(grown);
        ArrayPool<T>.Shared.Return(_items, clearArray: RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        _items = grown;
    }

    private static void ThrowOutOfRange() =>
        throw new ArgumentOutOfRangeException("index", "Index was out of range.");

    // ── Inner relay for item-change forwarding ─────────────────────────────

    private sealed class ItemChangedRelay : IChangedHandler
    {
        private readonly WeakReference<StateList<T>> _owner;

        internal ItemChangedRelay(StateList<T> owner) =>
            _owner = new WeakReference<StateList<T>>(owner);

        public void OnChanged(object source, in ChangedNotification notification)
        {
            if (_owner.TryGetTarget(out var list))
                list.NotifyAll(in notification);
        }
    }
}
