using System.Collections.Concurrent;
using ActualLab.Collections;
using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>
/// The RPC side of <see cref="IAuthorizationSignals"/> (authorization-consolidation 05 §1, moved from <c>CallContextSignals</c>):
/// a permission change invalidates the caller's pushed <see cref="ICallPermissions"/> snapshot (the client UI refreshes) and a
/// revocation closes the subject's connections (the reconnect is authenticated again). The server's per-connection caller cache
/// is versioned by <see cref="IPermissionVersions"/>, which the signals bumped before this runs.
/// </summary>
internal sealed class RpcAuthorizationChangeObserver(CallContextConnections connections, IServiceProvider services)
    : IAuthorizationChangeObserver
{
    public void OnChanged(in AuthorizationChange change)
    {
        if (Permissions is not { } permissions) return;
        if (change.Subject is { } subject) permissions.Invalidate(subject);
        else permissions.InvalidateAll();
    }

    public ValueTask OnRevokedAsync(string subject, CancellationToken cancellationToken)
        => new(connections.DisconnectAsync(subject, cancellationToken));

    // The DI instance is Fusion's class proxy (a CallPermissionsService subclass): its calls inside Invalidation.Begin() invalidate.
    private CallPermissionsService? Permissions => services.GetService<ICallPermissions>() as CallPermissionsService;
}

/// <summary>
/// Connection-bound capture (<c>FromConnection()</c>) and the subject → connections index used by revocation
/// (<see cref="IAuthorizationSignals.RevokedAsync"/>). Wraps
/// Fusion's <see cref="RpcPeerOptions.ServerConnectionFactory"/>, which receives the WebSocket upgrade
/// <see cref="HttpContext"/> in its properties.
/// </summary>
public sealed class CallContextConnections
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<RpcPeer, RpcConnection>> _bySubject = new(StringComparer.Ordinal);
    private readonly Func<HttpContext, bool, CallIdentity?>? _capture;
    private readonly bool _capturing;
    private readonly bool _tenant;

    internal CallContextConnections(CallContextServerBuilder options)
    {
        _capturing = options.CaptureConnection;
        _capture = options.ConnectionCapture;
        _tenant = options.Tenant;
    }

    /// <summary>Connections currently registered for <paramref name="subject"/>.</summary>
    public int CountFor(string subject)
    {
        if (!_bySubject.TryGetValue(subject, out var peers)) return 0;
        int n = 0;
        foreach (var (peer, connection) in peers)
            if (ReferenceEquals(peer.ConnectionState.Value.Connection, connection)) n++;
        return n;
    }

    /// <summary>Disconnects every current connection of <paramref name="subject"/>.</summary>
    public async Task DisconnectAsync(string subject, CancellationToken ct = default)
    {
        if (!_bySubject.TryRemove(subject, out var peers)) return;
        foreach (var (peer, connection) in peers)
        {
            var state = peer.ConnectionState;
            if (ReferenceEquals(state.Value.Connection, connection))
                await peer.Disconnect(null, state, ct).ConfigureAwait(false);
        }
    }

    internal RpcPeerOptions Wrap(RpcPeerOptions options)
    {
        if (!_capturing) return options;
        var inner = options.ServerConnectionFactory;
        return options with
        {
            ServerConnectionFactory = async (peer, transport, properties, ct) =>
            {
                CallIdentity? identity = null;
                if (properties.KeylessTryGet<HttpContext>(out var http))
                {
                    identity = _capture is not null ? _capture(http, _tenant) : Capture(http, _tenant);
                    if (identity is not null) properties = properties.KeylessSet(new ConnectionCallIdentity(identity));
                }
                var connection = await inner(peer, transport, properties, ct).ConfigureAwait(false);
                if (identity?.Subject is { } subject) Register(subject, peer, connection);
                return connection;
            },
        };
    }

    private void Register(string subject, RpcPeer peer, RpcConnection connection)
    {
        // A peer that reconnects under another user leaves a stale entry; DisconnectAsync compares with the current connection.
        var peers = _bySubject.GetOrAdd(subject, static _ => new());
        peers[peer] = connection;
    }

    /// <summary>
    /// Default capture: <see cref="HttpContext.User"/> (as authenticated by the host), the bearer token (Authorization
    /// header, else <c>access_token</c> query — browsers can't set WebSocket headers) and, with <c>WithTenant()</c>, the
    /// tenant the host resolved for the upgrade request (<see cref="IScopeContext"/>) — with the request's
    /// <see cref="YFex.Tenancy.Resolution.TenantImpersonation"/> when YFex.Web switched it to another tenant.
    /// </summary>
    public static CallIdentity Capture(HttpContext http, bool withTenant)
    {
        ArgumentNullException.ThrowIfNull(http);
        AccessToken? token = null;
        if (System.Net.Http.Headers.AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization, out var header)
            && header.Parameter is { Length: > 0 } value)
            token = new AccessToken(value, scheme: header.Scheme);
        else if (http.Request.Query["access_token"].ToString() is { Length: > 0 } queryToken)
            token = new AccessToken(queryToken);

        // EffectiveIdentity tolerates a missing IScopeContext (no tenancy registered → ScopeOverride or None).
        var scope = withTenant ? http.RequestServices?.GetService<IScopeContext>().EffectiveIdentity() ?? default : default;
        var impersonation = withTenant ? http.Features.Get<YFex.Tenancy.Resolution.TenantImpersonation>() : null;
        return new CallIdentity(http.User, token, scope, impersonation: impersonation);
    }
}
