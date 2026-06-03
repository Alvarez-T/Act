using System.Security.Claims;

namespace YFex.Cqrs;

/// <summary>Context available when computing a cache scope key.</summary>
public interface ICacheScopeContext
{
    ClaimsPrincipal User { get; }
    string? TenantId { get; }
    string? SessionId { get; }
}
