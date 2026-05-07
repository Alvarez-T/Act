namespace YFex.NavigatR;

internal sealed class NavigablePool
{
    private readonly int _capacity;
    private readonly LinkedList<NavigationEntry> _lru = new();

    public NavigablePool(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public void OnSuspended(NavigationEntry entry)
    {
        if (entry.State == NavigationEntryState.Pinned) return;
        _lru.AddLast(entry);
        TrimToCapacity();
    }

    public void OnActivated(NavigationEntry entry)
    {
        var node = _lru.Find(entry);
        if (node is not null) { _lru.Remove(node); _lru.AddLast(entry); }
    }

    public void Remove(NavigationEntry entry) => _lru.Remove(entry);

    private void TrimToCapacity()
    {
        int count = 0;
        foreach (var e in _lru)
            if (!e.IsKeepAlive) count++;

        var node = _lru.First;
        while (count > _capacity && node is not null)
        {
            var next = node.Next;
            var entry = node.Value;
            if (!entry.IsKeepAlive && entry.State == NavigationEntryState.Suspended)
            {
                _lru.Remove(node);
                entry.Release();
                count--;
            }
            node = next;
        }
    }
}