using ActualLab.Fusion;
using ActualLab.Rpc.Infrastructure;
using Microsoft.AspNetCore.Http;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>
/// A pluggable source of the caller's identity for an inbound RPC call (like YFex.Web's tenant strategies). Sources are
/// tried in order; the first one returning an AUTHENTICATED identity wins. When none does, the first unauthenticated
/// identity returned (it may still carry a tenant) — else anonymous — is used.
/// </summary>
public interface ICallIdentitySource
{
    /// <summary>
    /// The caller of <paramref name="call"/>, or null when this source has nothing to say. Return the SAME instance for
    /// the same caller (cache per connection / token / session) so its resolved permissions can be reused.
    /// </summary>
    ValueTask<CallIdentity?> ResolveAsync(RpcInboundCall call, CancellationToken ct);
}

/// <summary>
/// Validates a per-call token (header option) and the tenant scope the client asked for. Registered by the app — the
/// token format (JWT, opaque, …) is the host's business. Never trust <paramref name="requestedScope"/> blindly: it is
/// client input (grant it only when the principal may act in that tenant).
/// </summary>
public interface ICallTokenValidator
{
    /// <returns>The caller, or null to reject the token (the call then runs as anonymous).</returns>
    ValueTask<CallIdentity?> ValidateAsync(AccessToken token, ScopeIdentity requestedScope, CancellationToken ct);
}

/// <summary>
/// Server-side call-context configuration (validation D29):
/// <c>services.UseYFexMessagingRpcServer(rpc => rpc.CallContext(ctx => ctx.FromConnection().OrFromHeader("Authorization").WithTenant()))</c>.
/// Without a <c>CallContext(...)</c> call the server uses <see cref="FromConnection"/> (principal only).
/// </summary>
public sealed class CallContextServerBuilder
{
    internal List<Func<IServiceProvider, ICallIdentitySource>> Sources { get; } = [];
    internal bool CaptureConnection { get; private set; }
    internal Func<HttpContext, bool, CallIdentity?>? ConnectionCapture { get; private set; }
    internal bool Tenant { get; private set; }

    /// <summary>
    /// DEFAULT — connection-bound: the identity (<see cref="HttpContext.User"/> + bearer token, and the tenant with
    /// <see cref="WithTenant"/>) is captured ONCE when the WebSocket connects (the upgrade request went through the host's
    /// authentication and tenant middleware) and applied to every call on that connection. A reconnect re-captures it;
    /// <see cref="IAuthorizationSignals.RevokedAsync"/> forces one. Expired identities (token / <c>exp</c>) run as anonymous.
    /// </summary>
    /// <param name="capture">Custom capture (<c>(http, withTenant) => identity</c>); default: see remarks above.</param>
    public CallContextServerBuilder FromConnection(Func<HttpContext, bool, CallIdentity?>? capture = null)
    {
        CaptureConnection = true;
        ConnectionCapture = capture;
        Sources.Add(static _ => ConnectionIdentitySource.Instance);
        return this;
    }

    /// <summary>
    /// Per-call token header (client: <c>rpc.CallContext(ctx => ctx.SendToken(...))</c>), validated by the registered
    /// <see cref="ICallTokenValidator"/>. A present-but-invalid token makes the call anonymous (no fallback to later sources).
    /// Only REGULAR calls carry headers — Fusion compute-client calls (queries through <c>fusion.AddClient&lt;T&gt;()</c>)
    /// can't, so they rely on the connection identity.
    /// </summary>
    public CallContextServerBuilder OrFromHeader(string headerName = CallContextHeaders.Authorization)
    {
        Sources.Add(sp => new HeaderIdentitySource(headerName,
            sp.GetService(typeof(ICallTokenValidator)) as ICallTokenValidator
                ?? throw new InvalidOperationException(
                    $"CallContext: OrFromHeader(\"{headerName}\") needs an {nameof(ICallTokenValidator)} registration.")));
        return this;
    }

    /// <summary>Alias of <see cref="OrFromHeader"/> for a header-first chain.</summary>
    public CallContextServerBuilder FromHeader(string headerName = CallContextHeaders.Authorization) => OrFromHeader(headerName);

    /// <summary>
    /// Fusion <see cref="Session"/> (apps already using Fusion auth): the connection's session (Fusion's session-bound
    /// connection, <c>?session=</c> or the session middleware) is mapped to the caller by <paramref name="resolve"/>
    /// (e.g. through <c>IAuth.GetUser(session)</c>, itself a cached compute method). Called per call; cache inside.
    /// </summary>
    public CallContextServerBuilder OrFromSession(Func<IServiceProvider, Session, CancellationToken, ValueTask<CallIdentity?>> resolve)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        Sources.Add(sp => new SessionIdentitySource(sp, resolve));
        return this;
    }

    /// <summary>Adds a custom <see cref="ICallIdentitySource"/> (resolved per server).</summary>
    public CallContextServerBuilder OrFrom(Func<IServiceProvider, ICallIdentitySource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Sources.Add(source);
        return this;
    }

    /// <summary>
    /// Carries the caller's tenant: captured with the connection (the host's <see cref="IScopeContext"/> for the upgrade
    /// request — YFex.Web resolves it with its Header/Claim/Host strategies) or granted by the
    /// <see cref="ICallTokenValidator"/> for the header option. Every call then runs inside a <see cref="ScopeOverride"/>
    /// of that scope (an anonymous/tenant-less caller runs explicitly unscoped).
    /// </summary>
    public CallContextServerBuilder WithTenant()
    {
        Tenant = true;
        return this;
    }
}

/// <summary>Client-side call-context configuration — only needed for the per-call header option.</summary>
public sealed class CallContextClientBuilder
{
    internal Func<IServiceProvider, CancellationToken, ValueTask<string?>>? TokenProvider { get; private set; }
    internal string TokenHeader { get; private set; } = CallContextHeaders.Authorization;
    internal string TokenScheme { get; private set; } = "Bearer";
    internal bool Tenant { get; private set; }

    /// <summary>
    /// Sends <c>{scheme} {token}</c> in <paramref name="headerName"/> with every call made through
    /// <see cref="FusionMessageBus"/> (the provider is called per call — return a cached, refreshed token).
    /// </summary>
    public CallContextClientBuilder SendToken(
        Func<IServiceProvider, CancellationToken, ValueTask<string?>> tokenProvider,
        string headerName = CallContextHeaders.Authorization, string scheme = "Bearer")
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        TokenProvider = tokenProvider;
        TokenHeader = headerName;
        TokenScheme = scheme;
        return this;
    }

    /// <summary>Sends the client's ambient tenant (<see cref="IScopeContext"/>) as <see cref="CallContextHeaders.Scope"/>.</summary>
    public CallContextClientBuilder SendTenant()
    {
        Tenant = true;
        return this;
    }

    internal List<(string Name, Func<IServiceProvider, CancellationToken, ValueTask<string?>> Provider)> ExtraHeaders { get; } = [];

    /// <summary>
    /// Sends an additional per-call header (e.g. the installation assertion of YFex.Installations.Rpc) next to the token and the
    /// tenant, with every call made through <see cref="FusionMessageBus"/>. The provider is called per call (cache inside); null or
    /// empty = header omitted. Independent of <see cref="SendToken"/>, so libraries can add headers without taking the token slot.
    /// </summary>
    /// <exception cref="ArgumentException">A reserved header (token, scope, idempotency) or a header added twice.</exception>
    public CallContextClientBuilder SendHeader(string headerName, Func<IServiceProvider, CancellationToken, ValueTask<string?>> valueProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentNullException.ThrowIfNull(valueProvider);
        if (string.Equals(headerName, TokenHeader, StringComparison.OrdinalIgnoreCase)
            || string.Equals(headerName, CallContextHeaders.Scope, StringComparison.OrdinalIgnoreCase)
            || string.Equals(headerName, IdempotencyHeaders.Key, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{headerName}' is reserved by the call context.", nameof(headerName));
        foreach ((string name, _) in ExtraHeaders)
            if (string.Equals(name, headerName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Header '{headerName}' is already sent.", nameof(headerName));
        ExtraHeaders.Add((headerName, valueProvider));
        return this;
    }
}

/// <summary>Header names used by the per-call option.</summary>
public static class CallContextHeaders
{
    public const string Authorization = "Authorization";

    /// <summary>The requested tenant scope (<see cref="ScopeIdentity.ToString"/> form) — validated by <see cref="ICallTokenValidator"/>.</summary>
    public const string Scope = "yfex-scope";
}
