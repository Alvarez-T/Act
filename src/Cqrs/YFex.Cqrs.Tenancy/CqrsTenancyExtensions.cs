using Microsoft.Extensions.DependencyInjection;
using YFex.Tenancy;

namespace YFex.Cqrs.Tenancy;

/// <summary>Opt-in switch that feeds YFex.Cqrs query-cache scopes from YFex.Tenancy.</summary>
public static class CqrsTenancyExtensions
{
    /// <summary>
    /// Registers a <see cref="TenancyCacheScopeContext"/> as <see cref="ICacheScopeContext"/>, so
    /// <c>ScopedByTenant()</c> / <c>ScopedByUser()</c> queries key their cache entries by the scope in effect:
    /// <code>
    /// services.AddYFexTenancy(t => t.IsolateQueryCache(q => q.Tenant(MyScope.Tenant).User(MyScope.User)));
    /// </code>
    /// An <see cref="ICacheScopeContext"/> the host already registered is decorated, not replaced: it still supplies
    /// the session, and the user when <see cref="QueryCacheScopeOptions.User{TScope}"/> is not set. Fails closed
    /// (throws for a scoped query with nothing in scope) when the global isolation policy is <c>FailClosed()</c>.
    /// </summary>
    /// <remarks>
    /// Independent of <c>IsolatePersistence(o => o.IsolateCache = true)</c>, which partitions the whole cache. Use
    /// both for per-query granularity finer than the cache partition window.
    /// </remarks>
    public static YFexTenancyBuilder IsolateQueryCache(this YFexTenancyBuilder tenancy, Action<QueryCacheScopeOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(tenancy);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new QueryCacheScopeOptions();
        configure(options);

        var services = tenancy.Services;
        var inner = services.LastOrDefault(d => d.ServiceType == typeof(ICacheScopeContext) && !d.IsKeyedService);
        if (inner is not null) services.Remove(inner);

        services.Add(ServiceDescriptor.Describe(
            typeof(ICacheScopeContext),
            sp => new TenancyCacheScopeContext(
                options,
                sp.GetService<IScopeContext>(),
                inner is null ? null : ResolveInner(sp, inner),
                sp.GetService<ScopePartitionOptions>()?.WhenUnscoped ?? UnscopedBehavior.Shared),
            inner?.Lifetime ?? ServiceLifetime.Singleton));

        return tenancy;
    }

    private static ICacheScopeContext ResolveInner(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is ICacheScopeContext instance) return instance;
        if (descriptor.ImplementationFactory is { } factory) return (ICacheScopeContext)factory(sp);
        if (descriptor.ImplementationType is { } type) return (ICacheScopeContext)ActivatorUtilities.CreateInstance(sp, type);
        throw new InvalidOperationException("Unable to resolve the inner ICacheScopeContext to decorate.");
    }
}
