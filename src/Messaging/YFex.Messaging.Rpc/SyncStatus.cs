using System.ComponentModel;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Bindable singleton that exposes the current sync state for UI binding.
/// Updated by <see cref="OutboxReplayer"/> and the network-status projection.
/// </summary>
public sealed class SyncStatus : INotifyPropertyChanged
{
    private bool _isOffline;
    private bool _isSyncing;
    private int _pendingCommandCount;
    private DateTimeOffset? _lastSyncAt;
    private Exception? _lastSyncError;

    public bool IsOffline
    {
        get => _isOffline;
        internal set { if (_isOffline != value) { _isOffline = value; Raise(nameof(IsOffline)); } }
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        internal set { if (_isSyncing != value) { _isSyncing = value; Raise(nameof(IsSyncing)); } }
    }

    public int PendingCommandCount
    {
        get => _pendingCommandCount;
        internal set { if (_pendingCommandCount != value) { _pendingCommandCount = value; Raise(nameof(PendingCommandCount)); } }
    }

    public DateTimeOffset? LastSyncAt
    {
        get => _lastSyncAt;
        internal set { if (_lastSyncAt != value) { _lastSyncAt = value; Raise(nameof(LastSyncAt)); } }
    }

    public Exception? LastSyncError
    {
        get => _lastSyncError;
        internal set { if (_lastSyncError != value) { _lastSyncError = value; Raise(nameof(LastSyncError)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
