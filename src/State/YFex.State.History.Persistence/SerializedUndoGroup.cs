using MemoryPack;

namespace YFex.State.History.Persistence;

/// <summary>MemoryPack-serializable representation of one undo/redo group.</summary>
[MemoryPackable]
public partial record SerializedUndoGroup
{
    public string? Label { get; init; }
    public long TimestampTicks { get; init; }
    public SerializedUndoDelta[] Deltas { get; init; } = [];
}

/// <summary>MemoryPack-serializable representation of one property-change delta.</summary>
[MemoryPackable]
public partial record SerializedUndoDelta
{
    public string OwnerTypeName { get; init; } = "";
    public string PropertyName { get; init; } = "";

    /// <summary>JSON-encoded old value. Null when the value was null.</summary>
    public string? OldValueJson { get; init; }

    /// <summary>JSON-encoded new value. Null when the value was null.</summary>
    public string? NewValueJson { get; init; }

    /// <summary>Assembly-qualified type name of OldValue / NewValue. Used for deserialization.</summary>
    public string? ValueTypeName { get; init; }

    public long TimestampTicks { get; init; }
    public int MergeWindowMs { get; init; }
}
