using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using YFex.State.Collections;
using YFex.State.Notification;

namespace YFex.State.Mvvm.Collections;

/// <summary>
/// Adapter that translates <see cref="StateList{T}"/> notifications into
/// <see cref="INotifyCollectionChanged"/> and <see cref="INotifyPropertyChanged"/> events
/// for XAML binding engines (WPF, MAUI, WinUI). Marshals to the captured
/// <see cref="SynchronizationContext"/> when updates arrive from a background context.
/// </summary>
public sealed class StateCollection<T> :
    INotifyCollectionChanged, INotifyPropertyChanged,
    IList<T>, IList, IChangedHandler, IDisposable
{
    private readonly StateList<T> _source;
    private readonly SynchronizationContext? _syncContext;

    private static readonly SendOrPostCallback s_collectionChangedCallback = static state =>
    {
        var (view, args) = ((StateCollection<T>, NotifyCollectionChangedEventArgs))state!;
        view.CollectionChanged?.Invoke(view, args);
        view.PropertyChanged?.Invoke(view, new PropertyChangedEventArgs(nameof(Count)));
    };

    public StateCollection(StateList<T> source)
    {
        _source = source;
        _syncContext = SynchronizationContext.Current;
        _source.Subscribe(this);
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    // ── IChangedHandler ───────────────────────────────────────────────────────

    public void OnChanged(object source, in ChangedNotification n)
    {
        NotifyCollectionChangedEventArgs args = n.Kind switch
        {
            ChangeKind.ItemsAdded => new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add,
                GetRange(n.Index, n.Count),
                n.Index),
            ChangeKind.ItemsRemoved => new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove,
                GetRange(n.Index, n.Count),
                n.Index),
            ChangeKind.ItemReplaced => new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Replace,
                new[] { _source[n.Index] }, // newItems
                n.OldItem is null ? new T?[] { default } : new[] { (T)n.OldItem },
                n.Index),
            _ => new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset),
        };

        if (_syncContext is null || _syncContext == SynchronizationContext.Current)
        {
            CollectionChanged?.Invoke(this, args);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        }
        else
        {
            _syncContext.Post(s_collectionChangedCallback, (this, args));
        }
    }

    private List<T> GetRange(int index, int count)
    {
        var list = new List<T>(count);
        var span = _source.AsSpan();
        for (int i = index; i < index + count && i < span.Length; i++)
            list.Add(span[i]);
        return list;
    }

    // ── IList<T> / IReadOnlyList<T> ───────────────────────────────────────────

    public int Count => _source.Count;
    public bool IsReadOnly => false;

    public T this[int index]
    {
        get => _source[index];
        set => _source[index] = value;
    }

    public void Add(T item) => _source.Add(item);
    public void Clear() => _source.Clear();
    public bool Contains(T item) => _source.IndexOf(item) >= 0;
    public void CopyTo(T[] array, int arrayIndex) => _source.AsSpan().CopyTo(array.AsSpan(arrayIndex));
    public bool Remove(T item) => _source.Remove(item);
    public int IndexOf(T item) => _source.IndexOf(item);
    public void Insert(int index, T item) => throw new NotSupportedException("StateList does not support Insert; use Add or AddRange.");
    public void RemoveAt(int index) => _source.RemoveAt(index);
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_source).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // ── IList (non-generic, required by WPF ItemsControl) ────────────────────

    bool IList.IsFixedSize => false;
    bool IList.IsReadOnly => false;
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => _source[index];
        set => _source[index] = (T)value!;
    }

    int IList.Add(object? value) { _source.Add((T)value!); return _source.Count - 1; }
    bool IList.Contains(object? value) => value is T t && Contains(t);
    int IList.IndexOf(object? value) => value is T t ? IndexOf(t) : -1;
    void IList.Insert(int index, object? value) => Insert(index, (T)value!);
    void IList.Remove(object? value) { if (value is T t) Remove(t); }
    void IList.RemoveAt(int index) => RemoveAt(index);
    void ICollection.CopyTo(Array array, int index) => CopyTo((T[])array, index);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _source.Unsubscribe(this);
}
