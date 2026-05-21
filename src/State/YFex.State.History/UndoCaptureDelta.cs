namespace YFex.State.History;

/// <summary>
/// Serializable snapshot of one property-change delta.
/// <c>OldValue</c> / <c>NewValue</c> are <see langword="object?"/> — the persistence
/// layer uses a typed setter registry (emitted by <c>YFex.Persistence.Generator</c>)
/// to serialize and deserialize them without reflection per-restore.
/// </summary>
public sealed class UndoCaptureDelta
{
    /// <summary>Assembly-independent type name of the owner StateObject (e.g. the class full name).</summary>
    public string OwnerTypeName { get; }

    /// <summary>Name of the property that changed.</summary>
    public string PropertyName { get; }

    /// <summary>Value before the change (boxed). Null for reference types that were null.</summary>
    public object? OldValue { get; }

    /// <summary>Value after the change (boxed).</summary>
    public object? NewValue { get; }

    /// <summary>UTC ticks when the change was recorded.</summary>
    public long TimestampTicks { get; }

    /// <summary>Merge window used when the delta was recorded.</summary>
    public int MergeWindowMs { get; }

    public UndoCaptureDelta(
        string ownerTypeName,
        string propertyName,
        object? oldValue,
        object? newValue,
        long timestampTicks,
        int mergeWindowMs)
    {
        OwnerTypeName  = ownerTypeName;
        PropertyName   = propertyName;
        OldValue       = oldValue;
        NewValue       = newValue;
        TimestampTicks = timestampTicks;
        MergeWindowMs  = mergeWindowMs;
    }
}
