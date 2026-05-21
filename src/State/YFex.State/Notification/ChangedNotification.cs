namespace YFex.State.Notification;

/// <summary>
/// Passed by-in to <see cref="IChangedHandler.OnChanged"/> — zero heap allocation per fire.
/// Carries both a human-readable name (for INotifyPropertyChanged adapters) and a dense uint ID
/// (for fast bitmap routing). OldValue/NewValue are absent to avoid boxing value types.
/// Collection operations (StateList) set <see cref="Kind"/>, <see cref="Index"/>, and
/// <see cref="Count"/>; property notifications leave them at their defaults (0).
/// </summary>
public readonly struct ChangedNotification
{
    public required string PropertyName { get; init; }

    /// <summary>
    /// For <see cref="ChangeKind.ItemReplaced"/>: the previous item value at <see cref="Index"/>.
    /// Boxed via <see cref="object"/> so the descriptor stays generic over T. Null for non-replace
    /// notifications. Adapters that emit <c>NotifyCollectionChangedEventArgs(Replace, new, old, i)</c>
    /// (e.g. <c>StateCollection</c>) use this to satisfy WPF/MAUI binding contracts.
    /// </summary>
    public object? OldItem { get; init; }

    public required uint PropertyId { get; init; }

    /// <summary>Nature of the change. Defaults to <see cref="ChangeKind.PropertyChanged"/>.</summary>
    public ChangeKind Kind { get; init; }

    /// <summary>
    /// For <see cref="ChangeKind.ItemsAdded"/>, <see cref="ChangeKind.ItemsRemoved"/>,
    /// and <see cref="ChangeKind.ItemReplaced"/>: the zero-based index of the first affected item.
    /// Zero for other change kinds.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// For <see cref="ChangeKind.ItemsAdded"/> and <see cref="ChangeKind.ItemsRemoved"/>:
    /// the number of items affected. 1 for single-item operations and <see cref="ChangeKind.ItemReplaced"/>.
    /// Zero for other change kinds.
    /// </summary>
    public int Count { get; init; }
}
