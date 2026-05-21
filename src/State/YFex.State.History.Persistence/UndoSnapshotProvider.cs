using System.Text.Json;
using MemoryPack;
using YFex.Persistence;
using YFex.State.History;

namespace YFex.State.History.Persistence;

/// <summary>
/// <see cref="ISnapshotProvider"/> that saves and restores one <see cref="UndoContext"/>.
/// <para>
/// <b>AOT note:</b> the default value serialization uses <c>System.Text.Json</c> with dynamic type
/// resolution and is not AOT/trim compatible. Override <see cref="SerializeValue"/> and
/// <see cref="DeserializeValue"/> with source-generated JSON serializers for AOT deployments.
/// </para>
/// <para>
/// Values are serialized as JSON (System.Text.Json) which handles all primitive types
/// without MemoryPack source-gen requirements. For complex value types, the concrete
/// application can subclass and override <see cref="SerializeValue"/> / <see cref="DeserializeValue"/>.
/// </para>
/// <para>
/// Restoration requires two resolver callbacks:
/// <list type="bullet">
///   <item><see cref="OwnerResolver"/> — maps a type full name to the live instance.</item>
///   <item><see cref="SetterResolver"/> — maps (type full name, property name) to the setter delegate.</item>
/// </list>
/// These are typically provided by the Persistence generator's emitted registry.
/// </para>
/// </summary>
[System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Undo value deserialization uses Type.GetType() and System.Text.Json with dynamic types.")]
[System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Undo value serialization uses System.Text.Json with dynamic types.")]
public class UndoSnapshotProvider : ISnapshotProvider
{
    private readonly UndoContext _context;

    public string Discriminator { get; }
    public int Version          { get; }

    /// <summary>
    /// Maps an owner type's full name to the live instance. Return <see langword="null"/>
    /// if the instance is not available (the delta will be skipped on restore).
    /// </summary>
    public Func<string, object?> OwnerResolver { get; set; } = _ => null;

    /// <summary>
    /// Maps (ownerTypeName, propertyName) to the setter delegate.
    /// Return <see langword="null"/> to skip the delta.
    /// </summary>
    public Func<string, string, Action<object, object?>?> SetterResolver { get; set; } = (_, _) => null;

    public UndoSnapshotProvider(UndoContext context, string discriminator, int version = 1)
    {
        _context      = context;
        Discriminator = discriminator;
        Version       = version;
    }

    // ── ISnapshotProvider ────────────────────────────────────────────────────

    public ValueTask<byte[]?> CaptureAsync(CancellationToken ct = default)
    {
        var undoGroups = Serialize(_context.CaptureUndoStack());
        var redoGroups = Serialize(_context.CaptureRedoStack());

        if (undoGroups.Length == 0 && redoGroups.Length == 0)
            return ValueTask.FromResult<byte[]?>(null);

        var payload = new UndoSnapshotPayload { UndoGroups = undoGroups, RedoGroups = redoGroups };
        byte[] bytes = MemoryPackSerializer.Serialize(payload);
        return ValueTask.FromResult<byte[]?>(bytes);
    }

    public ValueTask RestoreAsync(byte[] data, int storedVersion, CancellationToken ct = default)
    {
        if (storedVersion != Version) return ValueTask.CompletedTask; // skip incompatible schema

        UndoSnapshotPayload? payload;
        try { payload = MemoryPackSerializer.Deserialize<UndoSnapshotPayload>(data); }
        catch { return ValueTask.CompletedTask; }
        if (payload is null) return ValueTask.CompletedTask;

        var undoGroups = Deserialize(payload.UndoGroups);
        var redoGroups = Deserialize(payload.RedoGroups);

        _context.RestoreStacks(undoGroups, redoGroups, SetterResolver, OwnerResolver);
        return ValueTask.CompletedTask;
    }

    // ── Serialization helpers ────────────────────────────────────────────────

    private SerializedUndoGroup[] Serialize(IReadOnlyList<UndoCaptureGroup> groups)
    {
        var result = new SerializedUndoGroup[groups.Count];
        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var deltas = new SerializedUndoDelta[g.Deltas.Count];
            for (int j = 0; j < g.Deltas.Count; j++)
            {
                var d = g.Deltas[j];
                deltas[j] = new SerializedUndoDelta
                {
                    OwnerTypeName = d.OwnerTypeName,
                    PropertyName  = d.PropertyName,
                    OldValueJson  = d.OldValue is null ? null : SerializeValue(d.OldValue),
                    NewValueJson  = d.NewValue is null ? null : SerializeValue(d.NewValue),
                    ValueTypeName = (d.OldValue ?? d.NewValue)?.GetType().AssemblyQualifiedName,
                    TimestampTicks = d.TimestampTicks,
                    MergeWindowMs  = d.MergeWindowMs,
                };
            }
            result[i] = new SerializedUndoGroup
            {
                Label          = g.Label,
                TimestampTicks = g.Timestamp.Ticks,
                Deltas         = deltas,
            };
        }
        return result;
    }

    private List<UndoCaptureGroup> Deserialize(SerializedUndoGroup[] groups)
    {
        var result = new List<UndoCaptureGroup>(groups.Length);
        foreach (var g in groups)
        {
            var deltas = new List<UndoCaptureDelta>(g.Deltas.Length);
            foreach (var d in g.Deltas)
            {
                object? oldValue = TryDeserializeValue(d.OldValueJson, d.ValueTypeName);
                object? newValue = TryDeserializeValue(d.NewValueJson, d.ValueTypeName);
                deltas.Add(new UndoCaptureDelta(
                    d.OwnerTypeName,
                    d.PropertyName,
                    oldValue,
                    newValue,
                    d.TimestampTicks,
                    d.MergeWindowMs));
            }
            result.Add(new UndoCaptureGroup(
                g.Label,
                new DateTime(g.TimestampTicks, DateTimeKind.Utc),
                deltas));
        }
        return result;
    }

    // ── Value serialization (overridable for custom types) ───────────────────

    protected virtual string SerializeValue(object value)
        => JsonSerializer.Serialize(value, value.GetType());

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Deserializes value types by assembly-qualified name")]
    private object? TryDeserializeValue(string? json, string? typeName)
    {
        if (json is null || typeName is null) return null;
        try
        {
            Type? type = Type.GetType(typeName);
            if (type is null) return null;
            return DeserializeValue(json, type);
        }
        catch { return null; }
    }

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Deserializes value types dynamically")]
    protected virtual object? DeserializeValue(string json, Type targetType)
        => JsonSerializer.Deserialize(json, targetType);
}
