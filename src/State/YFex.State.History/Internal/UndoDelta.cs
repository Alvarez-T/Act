namespace YFex.State.History.Internal;

/// <summary>
/// Records a single property value change for later undo/redo.
/// Old/new values are stored as <see langword="object?"/> (V1 boxing); V2 can use a
/// discriminated-union struct to avoid boxing common value types.
/// The setter delegate is a static lambda emitted by the generator — one allocation per
/// class, zero per recorded change.
/// </summary>
internal sealed class UndoDelta : UndoDeltaBase
{
    internal readonly object Owner;
    internal readonly string _propertyName;
    internal readonly System.Action<object, object?> Setter;
    internal readonly object? OldValue;
    internal object? NewValue;
    internal long TimestampTicks;
    internal readonly int MergeWindowMs;

    internal UndoDelta(
        object owner,
        string propertyName,
        System.Action<object, object?> setter,
        object? oldValue,
        object? newValue,
        long timestampTicks,
        int mergeWindowMs)
    {
        Owner          = owner;
        _propertyName  = propertyName;
        Setter         = setter;
        OldValue       = oldValue;
        NewValue       = newValue;
        TimestampTicks = timestampTicks;
        MergeWindowMs  = mergeWindowMs;
    }

    internal override string PropertyName => _propertyName;

    internal override void ApplyUndo() => Setter(Owner, OldValue);
    internal override void ApplyRedo() => Setter(Owner, NewValue);

    internal override bool CanMergeWith(UndoDeltaBase incoming)
    {
        if (incoming is not UndoDelta other) return false;
        if (!ReferenceEquals(Owner, other.Owner)) return false;
        if (_propertyName != other._propertyName) return false;
        if (MergeWindowMs <= 0) return false;
        long windowTicks = MergeWindowMs * System.TimeSpan.TicksPerMillisecond;
        return (other.TimestampTicks - TimestampTicks) <= windowTicks;
    }

    internal override void MergeWith(UndoDeltaBase incoming)
    {
        var other = (UndoDelta)incoming;
        NewValue       = other.NewValue;
        TimestampTicks = other.TimestampTicks;
    }
}
