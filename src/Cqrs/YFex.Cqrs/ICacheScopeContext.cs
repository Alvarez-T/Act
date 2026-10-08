namespace YFex.Cqrs;

/// <summary>Context available when computing a cache scope key.</summary>
/// <remarks>
/// <see cref="Subject"/> replaced <c>User : ClaimsPrincipal</c> (authorization-consolidation Phase 3, D19): the cache key needs a
/// stable, unique user key, not a display name. Without a registered context (or with a null <see cref="Subject"/>) the key uses
/// the subject of <c>YFex.Authorization.Public.ActorContext.Current</c>; a <c>ScopedByUser()</c> query with no subject at all is
/// not cached (fail closed: never shared across users).
/// </remarks>
public interface ICacheScopeContext
{
    /// <summary>The stable user key the cache entry belongs to (the actor's subject id), or null to use the ambient actor.</summary>
    string? Subject { get; }

    string? TenantId { get; }

    string? SessionId { get; }
}
