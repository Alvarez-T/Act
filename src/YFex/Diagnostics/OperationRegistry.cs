using System.Diagnostics.CodeAnalysis;

namespace YFex.Diagnostics;

/// <summary>
/// Process-wide list of <see cref="OperationDescriptor"/>s. Assigns each a dense <see cref="OperationDescriptor.Index"/> so
/// engines keep per-operation data in arrays. Registration happens from generated module initializers (cold path, locked);
/// reads are lock-free over a copy-on-write array.
/// </summary>
public static class OperationRegistry
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, OperationDescriptor> s_byName = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, OperationDescriptor> s_byEventId = [];
    private static readonly List<EventIdCollision> s_collisions = [];
    private static OperationDescriptor[] s_all = new OperationDescriptor[64];
    private static int s_count;

    /// <summary>Number of registered descriptors (indexes are 0..Count-1).</summary>
    public static int Count => Volatile.Read(ref s_count);

    /// <summary>
    /// Raised after each registration, outside the lock (engines grow their per-operation arrays). Handlers must not throw and
    /// must not register descriptors.
    /// </summary>
    public static event Action<OperationDescriptor>? Registered;

    /// <summary>
    /// Registers <paramref name="descriptor"/> and returns it (so generated code can assign the result). Registering the same
    /// instance twice is a no-op. A second descriptor with an existing name is kept (indexes stay dense) but name lookups keep
    /// returning the first one.
    /// </summary>
    public static TDescriptor Register<TDescriptor>(TDescriptor descriptor) where TDescriptor : OperationDescriptor
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (s_lock)
        {
            if (descriptor.Index >= 0) return descriptor;

            int index = s_count;
            OperationDescriptor[] all = s_all;
            if (index == all.Length)
            {
                var grown = new OperationDescriptor[all.Length * 2];
                all.CopyTo(grown, 0);
                all = grown;
            }
            descriptor.TryAssignIndex(index);
            all[index] = descriptor;
            s_byName.TryAdd(descriptor.Name, descriptor);
            if (!s_byEventId.TryAdd(descriptor.EventId, descriptor)
                && s_byEventId[descriptor.EventId] is { } first && !ReferenceEquals(first, descriptor)
                && !string.Equals(first.Name, descriptor.Name, StringComparison.Ordinal))
            {
                s_collisions.Add(new EventIdCollision(descriptor.EventId, first, descriptor));
            }
            Volatile.Write(ref s_all, all);
            Volatile.Write(ref s_count, index + 1);
        }
        Registered?.Invoke(descriptor);
        return descriptor;
    }

    /// <summary>Registers a plain message descriptor (generated <c>[Log]</c> methods).</summary>
    public static OperationDescriptor RegisterMessage(
        OperationKind kind,
        string name,
        string module,
        string? template,
        LogLevel level,
        FieldSpec[]? fields = null,
        uint eventId = 0)
        => Register(new OperationDescriptor(kind.IsEmpty ? OperationKind.Message : kind, name, module, level,
            OperationDescriptorFlags.Leaf, fields, template, eventId));

    /// <summary>The descriptor at <paramref name="index"/>; null when out of range.</summary>
    public static OperationDescriptor? Get(int index)
    {
        OperationDescriptor[] all = Volatile.Read(ref s_all);
        return (uint)index < (uint)Count && (uint)index < (uint)all.Length ? all[index] : null;
    }

    /// <summary>All registered descriptors (a stable snapshot; never contains nulls).</summary>
    public static ReadOnlySpan<OperationDescriptor> All
    {
        get
        {
            int count = Count;
            OperationDescriptor[] all = Volatile.Read(ref s_all);
            return all.AsSpan(0, Math.Min(count, all.Length));
        }
    }

    /// <summary>Finds a descriptor by its <see cref="OperationDescriptor.Name"/> (configuration uses names, never indexes).</summary>
    public static bool TryGet(string name, [NotNullWhen(true)] out OperationDescriptor? descriptor)
    {
        lock (s_lock) return s_byName.TryGetValue(name, out descriptor);
    }

    /// <summary>Finds a descriptor by its event id.</summary>
    public static bool TryGetByEventId(uint eventId, [NotNullWhen(true)] out OperationDescriptor? descriptor)
    {
        lock (s_lock) return s_byEventId.TryGetValue(eventId, out descriptor);
    }

    /// <summary>Distinct names that hash to the same event id (the engine reports them to its self-log; fix with <c>[LogId]</c>).</summary>
    public static EventIdCollision[] Collisions
    {
        get { lock (s_lock) return [.. s_collisions]; }
    }
}

/// <summary>Two descriptors with different names and the same <see cref="OperationDescriptor.EventId"/>.</summary>
public readonly record struct EventIdCollision(uint EventId, OperationDescriptor First, OperationDescriptor Second);
