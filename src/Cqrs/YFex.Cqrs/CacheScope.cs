namespace YFex.Cqrs;

public enum CacheScope
{
    /// <summary>Shared across all users and sessions.</summary>
    Global,
    /// <summary>Scoped to the authenticated user identity.</summary>
    User,
    /// <summary>Scoped to the current tenant.</summary>
    Tenant,
    /// <summary>Scoped to the current browser/app session.</summary>
    Session,
    /// <summary>Custom key derived from <see cref="ICacheScopeContext"/>.</summary>
    Custom
}
