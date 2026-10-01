using YFex.Tenancy;

namespace YFex.Persistence.Tenancy;

/// <summary>Options for <c>IsolatePersistence()</c>.</summary>
public sealed class PersistenceIsolationOptions
{
    /// <summary>
    /// Logical key prefixes that are device-global and never partitioned (e.g. <c>"device/"</c>,
    /// <c>"consent/"</c>). They bypass the partitioner entirely, including fail-closed mode.
    /// </summary>
    public List<string> SharedKeyPrefixes { get; } = [];

    /// <summary>
    /// Optional partition window for the stores (<see cref="IKeyValueStore"/>, <see cref="ISyncLocalStore"/>),
    /// applied to a copy of the global <see cref="ScopePartitionOptions"/> (e.g. persisted state per
    /// operator while the global policy is per store). Also used by the cache unless
    /// <see cref="CachePartition"/> is set. Null = the global <see cref="IScopePartitioner"/>.
    /// </summary>
    public Action<ScopePartitionOptions>? Partition { get; set; }

    /// <summary>
    /// Optional partition window for the <see cref="ICache"/> only, applied to a copy of the <b>global</b>
    /// options (not of <see cref="Partition"/>). Lets private drafts be per operator while a catalog cache
    /// is shared per store. Null = the cache follows <see cref="Partition"/>.
    /// </summary>
    public Action<ScopePartitionOptions>? CachePartition { get; set; }

    /// <summary>Also scope the registered <see cref="ICache"/>. Default true.</summary>
    public bool IsolateCache { get; set; } = true;
}

/// <summary>
/// Maps logical keys to physical, partitioned keys for the current scope. Shared by the store, sync-store
/// and cache decorators so all three partition identically.
/// </summary>
public sealed class PersistenceKeyScoper
{
    private readonly IScopePartitioner _partitioner;
    private readonly string[] _shared;

    public PersistenceKeyScoper(IScopePartitioner partitioner, IEnumerable<string>? sharedKeyPrefixes = null)
    {
        _partitioner = partitioner ?? throw new ArgumentNullException(nameof(partitioner));
        _shared = sharedKeyPrefixes?.ToArray() ?? [];
    }

    /// <summary>The partition prefix that applies to <paramref name="logicalKey"/> right now ("" when shared/unscoped).</summary>
    public string PrefixFor(string logicalKey)
    {
        foreach (var s in _shared)
            if (logicalKey.StartsWith(s, StringComparison.Ordinal))
                return string.Empty;
        return _partitioner.CurrentPrefix();
    }

    /// <summary>The physical key for <paramref name="logicalKey"/> in the current scope.</summary>
    public string Map(string logicalKey) => PrefixFor(logicalKey) + logicalKey;

    /// <summary>
    /// Converts physical keys found under <paramref name="partitionPrefix"/> back to logical keys. In the
    /// shared area (empty prefix) other partitions' keys are filtered out, so unscoped enumeration never
    /// sees tenant data.
    /// </summary>
    public IReadOnlyList<string> Unmap(IReadOnlyList<string> physicalKeys, string partitionPrefix)
    {
        var result = new List<string>(physicalKeys.Count);
        foreach (var k in physicalKeys)
        {
            if (partitionPrefix.Length == 0)
            {
                if (!_partitioner.IsPartitioned(k)) result.Add(k);
            }
            else if (k.StartsWith(partitionPrefix, StringComparison.Ordinal))
            {
                result.Add(k[partitionPrefix.Length..]);
            }
        }
        return result;
    }

    /// <summary>True when <paramref name="physicalKey"/> lives in some partition.</summary>
    public bool IsPartitioned(string physicalKey) => _partitioner.IsPartitioned(physicalKey);
}
