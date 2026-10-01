using System.Collections.Concurrent;
using ActualLab.Collections;
using ActualLab.Fusion;
using ActualLab.Rpc;
using ActualLab.Rpc.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using YFex.Authorization.Public;
using YFex.Tenancy;

namespace YFex.Messaging.Rpc;

/// <summary>
/// Cheap per-user permissions version (validation D29). The server resolves a caller's permissions once and reuses them
/// for every call until this version changes — one dictionary read per call instead of a permission-provider hit.
/// </summary>
public interface IPermissionsVersions
{
    /// <summary>Current version for <paramref name="subject"/> (monotonic; changes whenever its permissions may have changed).</summary>
    long Get(string subject);

    /// <summary>Marks <paramref name="subject"/>'s permissions as changed.</summary>
    void Bump(string subject);

    /// <summary>Marks every user's permissions as changed (role map / policy change).</summary>
    void BumpAll();
}

/// <summary>In-memory <see cref="IPermissionsVersions"/> (single server). A farm replaces it with a shared store or feeds
/// <see cref="CallContextSignals.PermissionsChanged"/> from its message bus on every node.</summary>
public sealed class PermissionsVersions : IPermissionsVersions
{
    private readonly ConcurrentDictionary<string, long> _versions = new(StringComparer.Ordinal);
    private long _global;

    public long Get(string subject)
        => Volatile.Read(ref _global) + (_versions.TryGetValue(subject, out var v) ? v : 0);

    public void Bump(string subject) => _versions.AddOrUpdate(subject, 1, static (_, v) => v + 1);

    public void BumpAll() => Interlocked.Increment(ref _global);
}

/// <summary>
/// Server-side signals of the call context (validation D29): permission changes and revocation. Call them from the code
/// that changes grants / roles / sessions (admin UI, identity provider webhook, logout).
/// </summary>
public sealed class CallContextSignals(IPermissionsVersions versions, CallContextConnections connections, IServiceProvider services)
{
    /// <summary>
    /// The user's permissions changed: the next call reloads them (per-connection cache is versioned) and the client's
    /// <see cref="ICallPermissions.GetMyPermissionsAsync"/> is invalidated so the UI refreshes. Connections stay open.
    /// </summary>
    public void PermissionsChanged(string subject)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        versions.Bump(subject);
        Permissions?.Invalidate(subject);
    }

    /// <summary>Every user's permissions changed (role map / policy change).</summary>
    public void AllPermissionsChanged()
    {
        versions.BumpAll();
        Permissions?.InvalidateAll();
    }

    /// <summary>
    /// Logout / revocation: bumps the version and CLOSES the user's connections. Clients reconnect automatically and the
    /// new WebSocket upgrade is authenticated again (a revoked token yields an anonymous caller).
    /// </summary>
    public Task RevokeAsync(string subject, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subject);
        PermissionsChanged(subject);
        return connections.DisconnectAsync(subject, ct);
    }

    // The DI instance is Fusion's class proxy (a CallPermissionsService subclass): its calls inside Invalidation.Begin() invalidate.
    private CallPermissionsService? Permissions => services.GetService<ICallPermissions>() as CallPermissionsService;
}

/// <summary>
/// Connection-bound capture (<c>FromConnection()</c>) and the subject → connections index used by revocation. Wraps
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
    /// tenant the host resolved for the upgrade request (<see cref="IScopeContext"/>).
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
        return new CallIdentity(http.User, token, scope);
    }
}
