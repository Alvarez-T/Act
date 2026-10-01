using System.Collections.Frozen;
using System.Security.Claims;
using YFex.Authorization;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The ambient identity of the message being processed (validation D29): WHO is calling (<see cref="Auth"/>: principal +
/// resolved permissions + token) and in WHICH tenant (<see cref="Scope"/>), with room for culture / correlation id.
/// Messages and handlers never carry any of it — like tenancy, it is implicit.
/// </summary>
/// <remarks>
/// <para>Set per inbound RPC call by <see cref="CallContextMiddleware"/> from the configured sources
/// (<see cref="CallContextServerBuilder"/>: connection-bound by default, per-call token header, Fusion <c>Session</c>).
/// Every dispatcher runs validation stage 2 against <see cref="CallAuthorization.CurrentAuth"/> — this context first,
/// then the registered <see cref="IPrincipalAccessor"/> — and <see cref="CallContextPrincipalAccessor"/> makes the same
/// identity visible to any other <see cref="IPrincipalAccessor"/> consumer.</para>
/// <para>Flows across <c>await</c> (an <see cref="AsyncLocal{T}"/>); <see cref="Enter"/> also enters the tenant as a
/// <see cref="ScopeOverride"/> when the source captured one, so YFex.Tenancy consumers see it through
/// <see cref="ScopeContextExtensions.EffectiveIdentity"/>.</para>
/// </remarks>
public sealed class CallContext
{
    private static readonly AsyncLocal<CallContext?> s_current = new();

    /// <summary>An unauthenticated, permission-less context — what a call with no identity runs as.</summary>
    public static IAuthContext Anonymous { get; } =
        new AuthContext(new ClaimsPrincipal(new ClaimsIdentity()), FrozenSet<PermissionKey>.Empty);

    /// <summary>The context of the call being processed, or null outside of a remote call.</summary>
    public static CallContext? Current => s_current.Value;

    public CallContext(IAuthContext auth, ScopeIdentity scope = default, bool hasScope = false,
        string? culture = null, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        Auth = auth;
        Scope = scope;
        HasScope = hasScope;
        Culture = culture;
        CorrelationId = correlationId;
        Subject = SubjectOf(auth.Principal);
    }

    /// <summary>Principal, resolved permissions and the raw token (for on-behalf-of propagation).</summary>
    public IAuthContext Auth { get; }

    /// <summary>The caller's tenant scope. Meaningful only when <see cref="HasScope"/>.</summary>
    public ScopeIdentity Scope { get; }

    /// <summary>True when the source resolved a tenant (<c>.WithTenant()</c>); <see cref="Enter"/> then overrides the ambient scope.</summary>
    public bool HasScope { get; }

    public string? Culture { get; }

    public string? CorrelationId { get; }

    /// <summary>Stable user key (<c>NameIdentifier</c>, else <c>sub</c>) of an authenticated caller; null when anonymous.</summary>
    public string? Subject { get; }

    /// <summary>
    /// Set by <see cref="CallContextMiddleware"/> after it ran stage 2 for this message instance outside Fusion's compute
    /// cache; <see cref="ValidatingHandlerInvoker"/> then skips its own stage 2 for the same instance.
    /// </summary>
    internal object? AuthorizedMessage { get; set; }

    /// <summary>Makes this context (and its tenant, when <see cref="HasScope"/>) ambient until the scope is disposed.</summary>
    public Activation Enter() => new(this);

    /// <summary>User key of <paramref name="principal"/> — same rule as YFex.Web's scope projection (user rung).</summary>
    public static string? SubjectOf(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return null;
        return principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value;
    }

    /// <summary>Restores the previous context (and tenant override) on dispose.</summary>
    public readonly struct Activation : IDisposable
    {
        private readonly CallContext? _previous;
        private readonly ScopeOverride.Handle _tenant;
        private readonly bool _entered;

        internal Activation(CallContext context)
        {
            _previous = s_current.Value;
            s_current.Value = context;
            _tenant = context.HasScope ? ScopeOverride.Enter(context.Scope) : default;
            _entered = true;
        }

        public void Dispose()
        {
            if (!_entered) return;
            _tenant.Dispose();
            s_current.Value = _previous;
        }
    }
}

/// <summary>
/// A resolved caller as produced by an identity source (connection capture, token validation, Fusion session). Instances
/// are cached by the sources (per connection / token / session), which lets the resolved <see cref="IAuthContext"/> be
/// reused until the caller's permissions version changes (<see cref="IPermissionsVersions"/>).
/// </summary>
public sealed class CallIdentity
{
    /// <summary>Marker of a presented-but-rejected credential: the call runs as anonymous and later sources are skipped.</summary>
    internal static readonly CallIdentity Rejected = new(new ClaimsPrincipal(new ClaimsIdentity()));

    public CallIdentity(ClaimsPrincipal principal, AccessToken? token = null, ScopeIdentity scope = default,
        string? culture = null, DateTimeOffset? expiresAt = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        Principal = principal;
        Token = token;
        Scope = scope;
        Culture = culture;
        ExpiresAt = expiresAt ?? token?.ExpiresAt ?? ExpiryOf(principal);
        Subject = CallContext.SubjectOf(principal);
    }

    public ClaimsPrincipal Principal { get; }
    public AccessToken? Token { get; }
    public ScopeIdentity Scope { get; }
    public string? Culture { get; }

    /// <summary>After this instant the identity is ignored (the call runs as anonymous). From the token / <c>exp</c> claim.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    public string? Subject { get; }

    public bool IsAuthenticated => Subject is not null || Principal.Identity?.IsAuthenticated == true;

    internal bool IsExpired(DateTimeOffset now) => ExpiresAt is { } at && at <= now;

    /// <summary>Last resolved auth context and the permissions version it was built for (see CallContextResolver).</summary>
    internal ResolvedAuth? Resolved;

    internal sealed record ResolvedAuth(IAuthContext Auth, long Version);

    private static DateTimeOffset? ExpiryOf(ClaimsPrincipal principal)
        => principal.FindFirst("exp")?.Value is { } exp && long.TryParse(exp, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}
