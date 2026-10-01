using System.Collections.Concurrent;
using System.Net.Http.Headers;
using ActualLab.Collections;
using ActualLab.Fusion;
using ActualLab.Fusion.Server.Rpc;
using ActualLab.Rpc.Infrastructure;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>The identity captured when the connection was established (stored in the connection's properties).</summary>
internal sealed class ConnectionCallIdentity(CallIdentity identity)
{
    public CallIdentity Identity { get; } = identity;
}

/// <summary><c>FromConnection()</c>: returns the identity captured by <see cref="CallContextConnections"/> at connect time.</summary>
internal sealed class ConnectionIdentitySource : ICallIdentitySource
{
    public static readonly ConnectionIdentitySource Instance = new();

    public ValueTask<CallIdentity?> ResolveAsync(RpcInboundCall call, CancellationToken ct)
        => new(call.Context.Peer.ConnectionState.Value.Connection?.Properties.KeylessGet<ConnectionCallIdentity>()?.Identity);
}

/// <summary>
/// <c>OrFromHeader(name)</c>: per-call token (and requested tenant, <see cref="CallContextHeaders.Scope"/>) validated by the
/// app's <see cref="ICallTokenValidator"/>. Validated identities are cached per (token, scope) until they expire.
/// </summary>
internal sealed class HeaderIdentitySource(string headerName, ICallTokenValidator validator) : ICallIdentitySource
{
    private const int MaxCached = 1024;
    private static readonly RpcHeaderKey ScopeKey = new(CallContextHeaders.Scope);
    private readonly RpcHeaderKey _key = new(headerName);
    private readonly ConcurrentDictionary<(string Token, string Scope), CallIdentity> _cache = new();

    public async ValueTask<CallIdentity?> ResolveAsync(RpcInboundCall call, CancellationToken ct)
    {
        var headers = call.Context.Message.Headers;
        var raw = headers.TryGet(_key);
        if (string.IsNullOrEmpty(raw)) return null;                 // no header → next source
        var scopeText = headers.TryGet(ScopeKey) ?? "";

        if (_cache.TryGetValue((raw, scopeText), out var cached) && !cached.IsExpired(DateTimeOffset.UtcNow))
            return cached;

        var token = AuthenticationHeaderValue.TryParse(raw, out var parsed) && parsed.Parameter is { Length: > 0 } value
            ? new AccessToken(value, scheme: parsed.Scheme)
            : new AccessToken(raw);
        ScopeIdentity.TryParse(scopeText, out var requested);   // malformed → None
        var identity = await validator.ValidateAsync(token, requested, ct).ConfigureAwait(false);
        if (identity is null) return CallIdentity.Rejected;       // present but invalid → anonymous, no fallback

        if (_cache.Count >= MaxCached) _cache.Clear();
        _cache[(raw, scopeText)] = identity;
        return identity;
    }
}

/// <summary>
/// <c>OrFromSession(resolve)</c>: the Fusion <see cref="Session"/> bound to the connection (Fusion's session-bound
/// connection factory) mapped to a caller by the app. The last identity per session is reused while the caller (subject +
/// scope) is unchanged, so its resolved permissions are too.
/// </summary>
internal sealed class SessionIdentitySource(
    IServiceProvider services,
    Func<IServiceProvider, Session, CancellationToken, ValueTask<CallIdentity?>> resolve) : ICallIdentitySource
{
    private const int MaxCached = 4096;
    private readonly ConcurrentDictionary<string, CallIdentity> _last = new(StringComparer.Ordinal);

    public async ValueTask<CallIdentity?> ResolveAsync(RpcInboundCall call, CancellationToken ct)
    {
        if (call.Context.Peer.ConnectionState.Value.Connection is not SessionBoundRpcConnection { Session: { } session })
            return null;
        var identity = await resolve(services, session, ct).ConfigureAwait(false);
        if (identity is null) return null;

        if (_last.TryGetValue(session.Id, out var last)
            && string.Equals(last.Subject, identity.Subject, StringComparison.Ordinal) && last.Scope == identity.Scope
            && !last.IsExpired(DateTimeOffset.UtcNow))
            return last;
        if (_last.Count >= MaxCached) _last.Clear();
        _last[session.Id] = identity;
        return identity;
    }
}
