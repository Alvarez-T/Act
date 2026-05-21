namespace YFex.State.Notification;

/// <summary>
/// Describes the nature of a state change. Default (<see cref="PropertyChanged"/>) is the
/// property notification path; collection-specific values are used by <c>StateList&lt;T&gt;</c>.
/// </summary>
public enum ChangeKind : byte
{
    /// <summary>A single property value changed. Default.</summary>
    PropertyChanged = 0,

    /// <summary>One or more items were added to a collection.</summary>
    ItemsAdded = 1,

    /// <summary>One or more items were removed from a collection.</summary>
    ItemsRemoved = 2,

    /// <summary>An item was replaced at a specific index.</summary>
    ItemReplaced = 3,

    /// <summary>All items were removed in a single operation.</summary>
    ItemsCleared = 4,

    /// <summary>The entire collection was reset (e.g., assigned a new backing array).</summary>
    ItemsReset = 5,
}
