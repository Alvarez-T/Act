namespace YFex.NavigatR;

internal sealed class NavigablePool : IDisposable
{
    private readonly int _capacity;
    private readonly LinkedList<NavigationEntry> _lru = new();
    private readonly TimeSpan? _suspendedTimeout;
    private readonly Dictionary<NavigationEntry, CancellationTokenSource> _suspendedTimers = new();

    public NavigablePool(int capacity, TimeSpan? suspendedTimeout = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _suspendedTimeout = suspendedTimeout;
    }

    public void OnSuspended(NavigationEntry entry)
    {
        if (entry.State == NavigationEntryState.Pinned) return;
        _lru.AddLast(entry);
        TrimToCapacity();
        StartSuspendedTimer(entry);
    }

    public void OnActivated(NavigationEntry entry)
    {
        CancelSuspendedTimer(entry);
        var node = _lru.Find(entry);
        if (node is not null) { _lru.Remove(node); _lru.AddLast(entry); }
    }

    public void Remove(NavigationEntry entry)
    {
        CancelSuspendedTimer(entry);
        _lru.Remove(entry);
    }

    private void StartSuspendedTimer(NavigationEntry entry)
    {
        if (_suspendedTimeout is null) return;
        if (entry.IsKeepAlive) return;

        CancelSuspendedTimer(entry);

        var cts = new CancellationTokenSource();
        _suspendedTimers[entry] = cts;

        _ = Task.Delay(_suspendedTimeout.Value, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            if (entry.State == NavigationEntryState.Suspended)
            {
                _lru.Remove(entry);
                _suspendedTimers.Remove(entry);
                entry.Release();
            }
        }, TaskScheduler.Default);
    }

    private void CancelSuspendedTimer(NavigationEntry entry)
    {
        if (_suspendedTimers.TryGetValue(entry, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _suspendedTimers.Remove(entry);
        }
    }

    private void TrimToCapacity()
    {
        int count = 0;
        foreach (var e in _lru)
            if (!e.IsKeepAlive && e.State == NavigationEntryState.Suspended) count++;

        var node = _lru.First;
        while (count > _capacity && node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            if (!entry.IsKeepAlive && entry.State == NavigationEntryState.Suspended)
            {
                _lru.Remove(node);
                CancelSuspendedTimer(entry);
                entry.Release();
                count--;
            }
            node = next;
        }
    }

    public void Dispose()
    {
        foreach (var cts in _suspendedTimers.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _suspendedTimers.Clear();
    }
}