using YFex.Cqrs;
using YFex.Cqrs.Runtime;
using YFex.Persistence;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Builds the cache key and tags for a query. Shared by <see cref="LocalDispatcher"/> and
/// <see cref="FusionMessageBus"/>.
/// </summary>
/// <remarks>
/// The key is <b>deterministic across processes and restarts</b>: it hashes the query's value-based
/// <see cref="object.ToString"/> (records emit all members) with FNV-1a, rather than
/// <see cref="object.GetHashCode"/> — whose string hashing is randomized per process, so persisted /
/// offline entries never matched after a restart. Format: <c>query:{TypeFullName}[:{scope}]:{hash}</c>;
/// the <c>query:{TypeFullName}</c> prefix stays prefix- and tag-compatible.
/// </remarks>
internal static class QueryCacheKey
{
    /// <summary>Deterministic, scope-aware cache key. Returns null for non-cacheable queries.</summary>
    public static string? For(Type queryType, object query, QueryPolicy? policy, IServiceProvider sp)
    {
        if (query is not ICacheable) return null;
        var hash = Fnv1a64(query.ToString() ?? string.Empty);
        var scope = ResolveScope(policy, sp);
        return scope is null
            ? $"query:{queryType.FullName}:{hash:x16}"
            : $"query:{queryType.FullName}:{scope}:{hash:x16}";
    }

    /// <summary>The coarse type-level tag carried by every cached variant of a query type.</summary>
    public static string TypeTag(Type queryType) => $"qt:{queryType.FullName}";

    /// <summary>The precise entity tag: the query type plus a per-entity key (for exact invalidation).</summary>
    public static string EntityTag(Type queryType, string key) => $"en:{queryType.FullName}:{key}";

    /// <summary>The tag a command invalidation targets: precise <c>en:{Type}:{key}</c> when the target
    /// declares an entity key, else the coarse <c>qt:{Type}</c>.</summary>
    public static string InvalidationTag(in InvalidationTarget target, object cmd)
        => target.TagKey is { } tk
            ? EntityTag(target.QueryType, tk(cmd))
            : TypeTag(target.QueryType);

    /// <summary>
    /// Cache options for a query write: TTL + the coarse type tag, plus a precise entity tag
    /// <c>en:{QueryType}:{key}</c> when the query declares one via <c>TaggedBy</c>.
    /// </summary>
    public static CacheEntryOptions Options(Type queryType, QueryPolicy? policy, object query)
    {
        string[] tags = policy?.TagKey is { } tk
            ? [TypeTag(queryType), EntityTag(queryType, tk(query))]
            : [TypeTag(queryType)];
        return new CacheEntryOptions
        {
            Duration = policy?.Cache?.AbsoluteExpiration,
            Tags = tags,
        };
    }

    private static string? ResolveScope(QueryPolicy? policy, IServiceProvider sp)
    {
        if (policy is null) return null;
        var ctx = sp.GetService(typeof(ICacheScopeContext)) as ICacheScopeContext;
        if (policy.ScopeKey is not null)
            return ctx is null ? null : policy.ScopeKey(ctx);
        return policy.Scope switch
        {
            CacheScope.User    => ctx?.User.Identity?.Name,
            CacheScope.Tenant  => ctx?.TenantId,
            CacheScope.Session => ctx?.SessionId,
            _                  => null, // Global
        };
    }

    /// <summary>FNV-1a over UTF-16 code units — deterministic and culture-invariant (no per-process randomization).</summary>
    private static ulong Fnv1a64(string s)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        ulong hash = offset;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            hash = (hash ^ (byte)(c & 0xFF)) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }
        return hash;
    }
}
