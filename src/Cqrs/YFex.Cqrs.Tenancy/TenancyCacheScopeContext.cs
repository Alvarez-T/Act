using System.Security.Claims;
using YFex.Tenancy;

namespace YFex.Cqrs.Tenancy;

/// <summary>Which scope levels feed <see cref="ICacheScopeContext.TenantId"/> and <see cref="ICacheScopeContext.User"/>.</summary>
public sealed class QueryCacheScopeOptions
{
    internal byte? TenantRank { get; private set; }
    internal byte? UserRank { get; private set; }

    /// <summary>
    /// <see cref="ICacheScopeContext.TenantId"/> = the scope chain up to (and including) <paramref name="level"/>,
    /// so tenant values that repeat under different parents stay distinct.
    /// </summary>
    public QueryCacheScopeOptions Tenant<TScope>(TScope level) where TScope : struct, Enum
    {
        TenantRank = ScopeValue.RankOf(level);
        return this;
    }

    /// <summary>
    /// <see cref="ICacheScopeContext.User"/> = a principal named by the scope chain up to <paramref name="level"/>.
    /// Leave unset to keep the host's own <see cref="ICacheScopeContext"/> principal.
    /// </summary>
    public QueryCacheScopeOptions User<TScope>(TScope level) where TScope : struct, Enum
    {
        UserRank = ScopeValue.RankOf(level);
        return this;
    }
}

/// <summary>
/// <see cref="ICacheScopeContext"/> fed by the identity in effect (<see cref="ScopeContextExtensions.EffectiveIdentity"/>),
/// so <c>ScopedByTenant()</c> / <c>ScopedByUser()</c> query caches follow the ambient scope and
/// <see cref="ScopeOverride"/>. Values the options don't claim come from the host's context (<paramref name="inner"/>) when present.
/// </summary>
/// <remarks>
/// Scope values are opaque cache-key segments (<c>{rank}={value}/…</c>, least-specific-first, escaped), not bare ids.
/// When no rung is in the window the value is <see langword="null"/>, and <c>QueryCacheKey</c> then uses the unscoped key.
/// Pass <see cref="UnscopedBehavior.Throw"/> to fail closed instead.
/// </remarks>
public sealed class TenancyCacheScopeContext : ICacheScopeContext
{
    private const string AuthenticationType = "YFex.Tenancy";
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    private readonly IScopeContext? _scope;
    private readonly ICacheScopeContext? _inner;
    private readonly ScopeKeyPartitioner? _tenant;
    private readonly ScopeKeyPartitioner? _user;

    public TenancyCacheScopeContext(
        QueryCacheScopeOptions options,
        IScopeContext? scope = null,
        ICacheScopeContext? inner = null,
        UnscopedBehavior whenUnscoped = UnscopedBehavior.Shared)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TenantRank is null && options.UserRank is null)
            throw new ArgumentException("Configure at least one of Tenant(level) or User(level).", nameof(options));

        _scope = scope;
        _inner = inner;
        _tenant = Window(options.TenantRank, whenUnscoped);
        _user = Window(options.UserRank, whenUnscoped);
    }

    /// <inheritdoc />
    public ClaimsPrincipal User
    {
        get
        {
            if (_user is null) return _inner?.User ?? Anonymous;
            var name = Chain(_user);
            return name is null
                ? Anonymous
                : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], AuthenticationType));
        }
    }

    /// <inheritdoc />
    public string? TenantId => _tenant is null ? _inner?.TenantId : Chain(_tenant);

    /// <inheritdoc />
    public string? SessionId => _inner?.SessionId;

    // The partitioner already orders, filters and escapes; strip its "~s/" root and trailing '/'.
    private string? Chain(ScopeKeyPartitioner window)
    {
        var prefix = window.PrefixFor(_scope.EffectiveIdentity());
        return prefix.Length == 0 ? null : prefix[ScopeKeyPartitioner.Root.Length..^1];
    }

    private static ScopeKeyPartitioner? Window(byte? maxRank, UnscopedBehavior whenUnscoped)
        => maxRank is { } rank
            ? new ScopeKeyPartitioner(new ScopePartitionOptions { MaxRank = rank, WhenUnscoped = whenUnscoped })
            : null;
}
